using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public sealed partial class ModpackImportService
{
    private static readonly Uri McimApi = new("https://mod.mcimirror.top/curseforge/v1/");
    private static readonly Uri CurseToolsApi = new("https://api.curse.tools/v1/");
    private static readonly Uri ModrinthHashesApi = new("https://api.modrinth.com/v2/version_files");
    private const string McimHost = "mod.mcimirror.top";
    private const string CurseForgeEdge = "edge.forgecdn.net";

    private async Task<PackFile[]> ResolveCurseForgeFilesAsync(ModpackArchive pack, bool includeOptional,
        string? localFilesDirectory, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var files = pack.CurseForgeFiles.Where(file => !file.Optional || includeOptional).ToArray();
        if (files.Length == 0) return [];
        Report(progress, "查询 CurseForge 文件", $"正在通过 Curse.Tools 查询 {files.Length} 个文件");
        var downloads = new List<PackFile>();
        var unavailable = new List<string>();
        var needsModrinth = new List<(PackFile File, string Id)>();
        var total = pack.Overrides.Sum(item => item.Entry.Length);
        foreach (var batch in files.Chunk(50))
        {
            var primary = await ReadCurseForgeEntriesAsync(CurseToolsApi, "mods/files",
                new { fileIds = batch.Select(file => file.File).ToArray() }, ct).ConfigureAwait(false);
            var absent = batch.Where(file => !primary.TryGetValue((file.Project, file.File), out var item)
                || ReadDownloadUri(item) is null).Select(file => file.File).ToArray();
            var mirror = absent.Length == 0 ? [] : await ReadCurseForgeEntriesAsync(McimApi, "mods/files",
                new { fileIds = absent }, ct).ConfigureAwait(false);
            var zipProjects = batch.Where(file =>
                ReadString(primary.GetValueOrDefault((file.Project, file.File)), "fileName")?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true
                || ReadString(mirror.GetValueOrDefault((file.Project, file.File)), "fileName")?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                .Select(file => file.Project).Distinct().ToArray();
            var folders = await ResolveCurseForgeFoldersAsync(zipProjects, ct).ConfigureAwait(false);

            foreach (var file in batch)
            {
                var id = $"{file.Project}/{file.File}";
                primary.TryGetValue((file.Project, file.File), out var main);
                mirror.TryGetValue((file.Project, file.File), out var backup);
                var item = main.ValueKind == JsonValueKind.Object ? main : backup;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    unavailable.Add(id);
                    continue;
                }
                var name = ReadString(item, "fileName") ?? "";
                var folder = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods" : folders.GetValueOrDefault(file.Project);
                if (folder is null || name.Contains('/') || (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException($"CurseForge 文件 {file.Project}/{file.File} 不是支持的模组、资源包或光影文件。");
                var target = ModpackPaths.Relative(folder + '/' + name);
                var (digest, length) = ReadFileFingerprint(item)!.Value;
                total += length;
                if (total > ModpackArchive.MaximumBytes) throw new InvalidDataException("整合包展开大小超过 32 GB。");
                var direct = ReadDownloadUri(main);
                var mirrorSource = ReadDownloadUri(backup) ?? direct;
                if (mirrorSource?.Host == CurseForgeEdge)
                    mirrorSource = new UriBuilder(mirrorSource) { Host = McimHost, Query = "" }.Uri;
                var sources = new[] { direct, mirrorSource }.OfType<Uri>().Distinct().ToArray();
                var local = localFilesDirectory is null ? null : FindLocalPackFile(localFilesDirectory, target, name);
                var planned = new PackFile(target, sources, digest, null, length, file.Optional, local);
                if (sources.Length == 0 && local is null) needsModrinth.Add((planned, id));
                else downloads.Add(planned);
            }
        }
        foreach (var batch in needsModrinth.Chunk(50))
        {
            using var matches = await FindModrinthFilesAsync(batch.Select(entry => entry.File.Sha1), ct).ConfigureAwait(false);
            foreach (var (file, id) in batch)
            {
                var source = GetModrinthFile(matches, file);
                if (source is null) unavailable.Add($"{Path.GetFileName(file.Path)}（{id}）");
                else downloads.Add(file with { Sources = [source] });
            }
        }
        if (unavailable.Count > 0)
        {
            var shown = string.Join("、", unavailable.Take(20));
            var remaining = unavailable.Count > 20 ? " 等" : "";
            throw new IOException($"下载源未能提供 {unavailable.Count} 个文件的下载地址或版本记录。请稍后重试，或从作者或 CurseForge 获取原版本后使用「选择补充文件夹」。\n{shown}{remaining}");
        }
        ModpackArchive.CheckPaths(downloads.Select(file => file.Path), pack.Overrides);
        return downloads.ToArray();
    }

    private async Task DownloadCurseForgePackFileAsync(PackFile file, string target, CancellationToken ct)
    {
        try
        {
            await DownloadAsync(file.Sources, target, file.Bytes, ModpackArchive.MaximumFileBytes,
                file.Sha1, null, uri => IsCurseForgeDownload(uri) || IsModrinthFallbackFile(uri), ct).ConfigureAwait(false);
        }
        catch (IOException) when (!ct.IsCancellationRequested && file.Sources.Any(IsCurseForgeDownload))
        {
            using var matches = await FindModrinthFilesAsync([file.Sha1], ct).ConfigureAwait(false);
            var source = GetModrinthFile(matches, file);
            if (source is null) throw;
            await DownloadAsync([source], target, file.Bytes, ModpackArchive.MaximumFileBytes,
                file.Sha1, null, IsModrinthFallbackFile, ct).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<int, string>> ResolveCurseForgeFoldersAsync(int[] ids, CancellationToken ct)
    {
        var folders = new Dictionary<int, string>();
        foreach (var api in new[] { CurseToolsApi, McimApi })
        {
            var remaining = ids.Where(id => !folders.ContainsKey(id)).ToArray();
            if (remaining.Length == 0) break;
            var entries = await ReadCurseForgeEntriesAsync(api, "mods", new { modIds = remaining }, ct).ConfigureAwait(false);
            foreach (var (key, project) in entries)
            {
                var site = project.TryGetProperty("links", out var links) ? ReadString(links, "websiteUrl") : null;
                if (!Uri.TryCreate(site, UriKind.Absolute, out var website) || website.Scheme != "https"
                    || website.Host is not ("www.curseforge.com" or "curseforge.com")) continue;
                var parts = website.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts is not ["minecraft", var category, _]) continue;
                var folder = category switch { "texture-packs" => "resourcepacks", "shaders" => "shaderpacks", _ => null };
                if (folder is not null) folders.TryAdd(key.Id, folder);
            }
        }
        return folders;
    }

    private async Task<Dictionary<(int Project, int Id), JsonElement>> ReadCurseForgeEntriesAsync(
        Uri api, string endpoint, object body, CancellationToken ct)
    {
        using var doc = await ReadDownloadSourceAsync(new Uri(api, endpoint), body, ct).ConfigureAwait(false);
        var entries = new Dictionary<(int, int), JsonElement>();
        if (doc?.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array) return entries;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var fileId)) continue;
            if (endpoint == "mods") entries.TryAdd((fileId, fileId), entry.Clone());
            else if (entry.TryGetProperty("modId", out var project) && project.ValueKind == JsonValueKind.Number
                && project.TryGetInt32(out var projectId) && ReadFileFingerprint(entry) is not null
                && !string.IsNullOrWhiteSpace(ReadString(entry, "fileName"))) entries.TryAdd((projectId, fileId), entry.Clone());
        }
        return entries;
    }

    private static (string Sha1, long Bytes)? ReadFileFingerprint(JsonElement item)
    {
        if (!item.TryGetProperty("fileLength", out var length) || length.ValueKind != JsonValueKind.Number
            || !length.TryGetInt64(out var bytes) || bytes is < 1 or > ModpackArchive.MaximumFileBytes
            || !item.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Array) return null;
        foreach (var hash in hashes.EnumerateArray())
        {
            if (hash.ValueKind != JsonValueKind.Object || !hash.TryGetProperty("algo", out var algorithm)
                || algorithm.ValueKind != JsonValueKind.Number || !algorithm.TryGetInt32(out var value) || value != 1) continue;
            var digest = ReadString(hash, "value");
            if (ModpackPaths.IsHex(digest, 40)) return (digest!, bytes);
        }
        return null;
    }

    private Task<JsonDocument?> FindModrinthFilesAsync(IEnumerable<string> hashes, CancellationToken ct) =>
        ReadDownloadSourceAsync(ModrinthHashesApi, new
        {
            hashes = hashes.Select(hash => hash.ToLowerInvariant()).Distinct().ToArray(),
            algorithm = "sha1"
        }, ct);

    private static Uri? GetModrinthFile(JsonDocument? matches, PackFile file)
    {
        if (matches?.RootElement.ValueKind != JsonValueKind.Object
            || !matches.RootElement.TryGetProperty(file.Sha1.ToLowerInvariant(), out var version)
            || version.ValueKind != JsonValueKind.Object || !version.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array) return null;
        foreach (var candidate in files.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object || !candidate.TryGetProperty("hashes", out var hashes)
                || !string.Equals(ReadString(hashes, "sha1"), file.Sha1, StringComparison.OrdinalIgnoreCase)
                || !candidate.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                || !size.TryGetInt64(out var bytes) || bytes != file.Bytes
                || !Uri.TryCreate(ReadString(candidate, "url"), UriKind.Absolute, out var uri) || !IsModrinthFallbackFile(uri)) continue;
            return uri;
        }
        return null;
    }

    private static string? FindLocalPackFile(string root, string relative, string name)
    {
        var nested = ModpackPaths.SafeTarget(root, relative);
        if (File.Exists(nested)) return nested;
        var flat = ModpackPaths.SafeTarget(root, name);
        return File.Exists(flat) ? flat : null;
    }

    private async Task<JsonDocument?> ReadDownloadSourceAsync(Uri endpoint, object body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.UserAgent.ParseAdd("MCL/0.5");
        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await CopyAsync(input, buffer, 8 * 1024 * 1024, null, null, null, timeout.Token).ConfigureAwait(false);
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;

    private static Uri? ReadDownloadUri(JsonElement item) =>
        Uri.TryCreate(ReadString(item, "downloadUrl"), UriKind.Absolute, out var uri) && IsCurseForgeDownload(uri) ? uri : null;

    private static bool IsHttpsFile(Uri uri) =>
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsModrinthFallbackFile(Uri uri) =>
        IsHttpsFile(uri) && uri.Host == "cdn.modrinth.com" && uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal);

    private static bool IsCurseForgeDownload(Uri uri) => IsHttpsFile(uri)
        && uri.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal)
        && uri.Host is McimHost or "mediafilez.forgecdn.net" or "mediafiles.forgecdn.net" or CurseForgeEdge or "media.forgecdn.net";
}
