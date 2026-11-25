using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public sealed partial class ModpackImportService
{
    private async Task<PackFile[]> ResolveCurseForgeFilesAsync(ModpackArchive pack, bool includeOptional,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var files = pack.CurseForgeFiles.Where(file => !file.Optional || includeOptional).ToArray();
        if (files.Length == 0) return [];
        var key = Environment.GetEnvironmentVariable("MCL_CURSEFORGE_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException("此整合包需要从 CurseForge 下载模组。请先配置官方 API 密钥（环境变量 MCL_CURSEFORGE_API_KEY），再重启 MCL。");

        Report(progress, "查询 CurseForge 文件", $"正在获取 {files.Length} 个文件的下载信息");
        var downloads = new List<PackFile>();
        var unavailable = new List<string>();
        var total = pack.Overrides.Sum(item => item.Entry.Length);
        foreach (var batch in files.Chunk(50))
        {
            using var doc = await ReadCurseForgeAsync("mods/files", key, new { fileIds = batch.Select(file => file.File).ToArray() }, ct).ConfigureAwait(false);
            var resolved = doc.RootElement.GetProperty("data").EnumerateArray()
                .ToDictionary(item => (item.GetProperty("modId").GetInt32(), item.GetProperty("id").GetInt32()));
            var zipProjects = resolved.Where(entry => entry.Value.GetProperty("fileName").GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                .Select(entry => entry.Key.Item1).Distinct().ToArray();
            var folders = new Dictionary<int, string>();
            if (zipProjects.Length > 0)
            {
                using var projects = await ReadCurseForgeAsync("mods", key, new { modIds = zipProjects }, ct).ConfigureAwait(false);
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
                if (!resolved.TryGetValue((file.Project, file.File), out var item)
                    || !item.TryGetProperty("downloadUrl", out var download)
                    || !Uri.TryCreate(download.GetString(), UriKind.Absolute, out var uri) || !IsCurseForgeDownload(uri))
                {
                    unavailable.Add($"{file.Project}/{file.File}");
                    continue;
                }
                var name = item.GetProperty("fileName").GetString() ?? "";
                var folder = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods" : folders.GetValueOrDefault(file.Project);
                if (folder is null || name.Contains('/') || (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException($"CurseForge 文件 {file.Project}/{file.File} 不是支持的模组、资源包或光影文件。");
                var target = ModpackPaths.Relative(folder + '/' + name);
                var hashes = item.GetProperty("hashes").EnumerateArray();
                var sha1 = hashes.FirstOrDefault(hash => hash.GetProperty("algo").GetInt32() == 1);
                var digest = sha1.ValueKind == JsonValueKind.Object ? sha1.GetProperty("value").GetString() : null;
                var length = item.GetProperty("fileLength").GetInt64();
                if (!ModpackPaths.IsHex(digest, 40) || length is < 1 or > ModpackArchive.MaximumFileBytes)
                    throw new InvalidDataException($"CurseForge 文件 {file.Project}/{file.File} 缺少有效的大小或校验信息。");
                total += length;
                if (total > ModpackArchive.MaximumBytes) throw new InvalidDataException("整合包展开大小超过 32 GB。");
                downloads.Add(new(target, [uri], digest!, null, length, file.Optional));
            }
        }
        if (unavailable.Count > 0)
            throw new IOException($"CurseForge 未提供 {unavailable.Count} 个文件的下载链接。项目/文件编号：{string.Join("、", unavailable.Take(8))}。请联系整合包作者或使用 CurseForge 客户端安装。");
        ModpackArchive.CheckPaths(downloads.Select(file => file.Path), pack.Overrides);
        return downloads.ToArray();
    }

    private async Task<JsonDocument> ReadCurseForgeAsync(string endpoint, string key, object body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.curseforge.com/v1/" + endpoint);
        request.Headers.Add("x-api-key", key);
        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidDataException("CurseForge 没有接受 API 密钥，请检查密钥和该应用的访问权限。");
            if (!response.IsSuccessStatusCode)
                throw new IOException($"CurseForge 文件查询失败（HTTP {(int)response.StatusCode}）。请稍后重试。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await CopyAsync(input, buffer, 8 * 1024 * 1024, null, null, null, timeout.Token).ConfigureAwait(false);
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("连接 CurseForge 查询文件超时。", ex);
        }
    }

    private static bool IsCurseForgeDownload(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Host is "mediafilez.forgecdn.net" or "mediafiles.forgecdn.net" or "edge.forgecdn.net" or "media.forgecdn.net";
}
