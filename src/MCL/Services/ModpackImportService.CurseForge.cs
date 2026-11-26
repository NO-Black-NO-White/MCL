using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public sealed partial class ModpackImportService
{
    private static readonly Uri McimApi = new("https://mod.mcimirror.top/curseforge/v1/");
    private const string McimHost = "mod.mcimirror.top";

    private async Task<PackFile[]> ResolveCurseForgeFilesAsync(ModpackArchive pack, bool includeOptional,
        string? localFilesDirectory, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var files = pack.CurseForgeFiles.Where(file => !file.Optional || includeOptional).ToArray();
        if (files.Length == 0) return [];
        Report(progress, "查询 CurseForge 文件", $"正在通过 MCIM 查询 {files.Length} 个文件");
        var downloads = new List<PackFile>();
        var unavailable = new List<string>();
        var total = pack.Overrides.Sum(item => item.Entry.Length);
        foreach (var batch in files.Chunk(50))
        {
            using var doc = await ReadMcimAsync("mods/files", new { fileIds = batch.Select(file => file.File).ToArray() }, ct).ConfigureAwait(false);
            var resolved = doc.RootElement.GetProperty("data").EnumerateArray()
                .ToDictionary(item => (item.GetProperty("modId").GetInt32(), item.GetProperty("id").GetInt32()));
            var zipProjects = resolved.Where(entry => entry.Value.GetProperty("fileName").GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                .Select(entry => entry.Key.Item1).Distinct().ToArray();
            var folders = new Dictionary<int, string>();
            if (zipProjects.Length > 0)
            {
                using var projects = await ReadMcimAsync("mods", new { modIds = zipProjects }, ct).ConfigureAwait(false);
                foreach (var project in projects.RootElement.GetProperty("data").EnumerateArray())
                {
                    var site = project.GetProperty("links").GetProperty("websiteUrl").GetString();
                    if (!Uri.TryCreate(site, UriKind.Absolute, out var website) || website.Scheme != "https"
                        || website.Host is not ("www.curseforge.com" or "curseforge.com")) continue;
                    var parts = website.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts is not ["minecraft", var category, _]) continue;
                    var folder = category switch { "texture-packs" => "resourcepacks", "shaders" => "shaderpacks", _ => null };
                    if (folder is not null) folders.Add(project.GetProperty("id").GetInt32(), folder);
                }
            }
            foreach (var file in batch)
            {
                var id = $"{file.Project}/{file.File}";
                if (!resolved.TryGetValue((file.Project, file.File), out var item))
                {
                    unavailable.Add(id);
                    continue;
                }
                var name = item.GetProperty("fileName").GetString() ?? "";
                var folder = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods" : folders.GetValueOrDefault(file.Project);
                if (folder is null || name.Contains('/') || (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException($"CurseForge 文件 {file.Project}/{file.File} 不是支持的模组、资源包或光影文件。");
                var target = ModpackPaths.Relative(folder + '/' + name);
                var source = item.TryGetProperty("downloadUrl", out var download)
                    && Uri.TryCreate(download.GetString(), UriKind.Absolute, out var uri) && IsCurseForgeDownload(uri)
                    ? uri.Host == "edge.forgecdn.net" ? new UriBuilder(uri) { Host = McimHost, Query = "" }.Uri : uri
                    : null;
                var local = localFilesDirectory is null ? null : FindLocalPackFile(localFilesDirectory, target, name);
                if (source is null && local is null)
                {
                    unavailable.Add($"{name}（{id}）");
                    continue;
                }
                var hashes = item.GetProperty("hashes").EnumerateArray();
                var sha1 = hashes.FirstOrDefault(hash => hash.GetProperty("algo").GetInt32() == 1);
                var digest = sha1.ValueKind == JsonValueKind.Object ? sha1.GetProperty("value").GetString() : null;
                var length = item.GetProperty("fileLength").GetInt64();
                if (!ModpackPaths.IsHex(digest, 40) || length is < 1 or > ModpackArchive.MaximumFileBytes)
                    throw new InvalidDataException($"CurseForge 文件 {file.Project}/{file.File} 缺少有效的大小或校验信息。");
                total += length;
                if (total > ModpackArchive.MaximumBytes) throw new InvalidDataException("整合包展开大小超过 32 GB。");
                downloads.Add(new(target, source is null ? [] : [source], digest!, null, length, file.Optional, local));
            }
        }
        if (unavailable.Count > 0)
        {
            var shown = string.Join("、", unavailable.Take(20));
            var remaining = unavailable.Count > 20 ? " 等" : "";
            throw new IOException($"MCIM 缺少 {unavailable.Count} 个文件的下载地址或版本记录。请从作者或 CurseForge 获取原版本，使用「选择补充文件夹」后重试。\n{shown}{remaining}");
        }
        ModpackArchive.CheckPaths(downloads.Select(file => file.Path), pack.Overrides);
        return downloads.ToArray();
    }

    private static string? FindLocalPackFile(string root, string relative, string name)
    {
        var nested = ModpackPaths.SafeTarget(root, relative);
        if (File.Exists(nested)) return nested;
        var flat = ModpackPaths.SafeTarget(root, name);
        return File.Exists(flat) ? flat : null;
    }

    private async Task<JsonDocument> ReadMcimAsync(string endpoint, object body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(McimApi, endpoint));
        request.Headers.UserAgent.ParseAdd("MCL/0.5");
        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new IOException("MCIM 暂未允许本次查询，请稍后重试。");
            if (!response.IsSuccessStatusCode)
                throw new IOException($"MCIM 文件查询失败（HTTP {(int)response.StatusCode}）。请稍后重试。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await CopyAsync(input, buffer, 8 * 1024 * 1024, null, null, null, timeout.Token).ConfigureAwait(false);
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("连接 MCIM 查询文件超时。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException("无法连接 MCIM，请检查网络后重试。", ex);
        }
    }

    private static bool IsCurseForgeDownload(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal)
        && uri.Host is McimHost or "mediafilez.forgecdn.net" or "mediafiles.forgecdn.net" or "edge.forgecdn.net" or "media.forgecdn.net";
}
