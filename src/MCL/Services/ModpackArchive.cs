using System.IO.Compression;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

internal sealed record PackFile(string Path, Uri[] Sources, string Sha1, string? Sha512, long Bytes, bool Optional, string? LocalSource = null);
internal sealed record CurseForgeFile(int Project, int File, bool Optional);
internal sealed record PackOverride(string Path, ZipArchiveEntry Entry);

internal sealed class ModpackArchive
{
    internal const long MaximumBytes = 32L * 1024 * 1024 * 1024;
    internal const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com",
        "release-assets.githubusercontent.com", "objects.githubusercontent.com", "media.githubusercontent.com", "gitlab-static.net"
    };

    private ModpackArchive(ModpackPreview preview, PackFile[] files, PackOverride[] overrides, CurseForgeFile[]? curseForgeFiles = null)
    {
        Preview = preview;
        Files = files;
        Overrides = overrides;
        CurseForgeFiles = curseForgeFiles ?? [];
    }

    internal ModpackPreview Preview { get; }
    internal PackFile[] Files { get; }
    internal PackOverride[] Overrides { get; }
    internal CurseForgeFile[] CurseForgeFiles { get; }

    internal static ModpackArchive Read(ZipArchive zip, string path, string hash, CancellationToken cancellation)
    {
        try
        {
            return ReadCore(zip, path, hash, cancellation);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("整合包清单损坏，或缺少必要的版本和文件信息。", error);
        }
    }

    private static ModpackArchive ReadCore(ZipArchive zip, string path, string hash, CancellationToken cancellation)
    {
        if (zip.Entries.Count > 50000) throw new InvalidDataException("整合包中的文件数量过多。");
        var indexes = zip.Entries.Where(e => IsIndex(e.FullName, "modrinth.index.json")).ToArray();
        if (indexes.Length == 0)
        {
            var manifests = zip.Entries.Where(e => IsIndex(e.FullName, "manifest.json")).ToArray();
            if (manifests.Length == 1) return ReadCurseForge(zip, manifests[0], path, hash, cancellation);
            if (manifests.Length > 1) throw new InvalidDataException("此 ZIP 包含多个 CurseForge 清单。");
            throw new NotSupportedException("没有找到 Modrinth 或 CurseForge 客户端整合包清单。");
        }
        if (indexes.Length != 1) throw new InvalidDataException("压缩包包含多个 Modrinth 清单，请使用单个整合包。");
        var index = indexes[0];
        ModpackPaths.Relative(index.FullName);
        EnsureRegularFile(index);
        if (index.Length is < 1 or > 8 * 1024 * 1024) throw new InvalidDataException("Modrinth 清单大小无效。");
        var prefix = index.FullName[..^"modrinth.index.json".Length];
        using var stream = index.Open();
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != 1 || root.GetProperty("game").GetString() != "minecraft")
            throw new NotSupportedException("此包不是受支持的 Minecraft Java 版 Modrinth 格式。");
        var name = Label(root, "name");
        var version = Label(root, "versionId");
        var dependencies = root.GetProperty("dependencies");
        var minecraft = dependencies.GetProperty("minecraft").GetString();
        if (!ModpackPaths.IsGameVersion(minecraft)) throw new NotSupportedException("当前可导入 Minecraft 1.13 及以后的正式版整合包。");
        string? fabric = null;
        var dependencyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in dependencies.EnumerateObject())
        {
            if (!dependencyNames.Add(entry.Name)) throw new InvalidDataException("整合包清单重复声明版本依赖。");
            if (entry.Name == "minecraft") continue;
            if (entry.Name != "fabric-loader")
                throw new NotSupportedException($"此包需要 {DisplayDependency(entry.Name)}，当前只支持原版和 Fabric 整合包。");
            fabric = entry.Value.GetString();
            if (!ModpackPaths.IsLoaderVersion(fabric)) throw new NotSupportedException("此包使用的 Fabric Loader 版本格式暂不支持。");
        }
        var items = root.GetProperty("files");
        if (items.GetArrayLength() > 20000) throw new InvalidDataException("整合包要求下载的文件过多。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<PackFile>();
        long downloadBytes = 0;
        foreach (var item in items.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            string? client = null;
            if (item.TryGetProperty("env", out var env)) client = env.GetProperty("client").GetString();
            if (client == "unsupported") continue;
            if (client is not (null or "required" or "optional")) throw new InvalidDataException("整合包有未知的客户端文件类型。");
            var target = ModpackPaths.Relative(item.GetProperty("path").GetString() ?? "");
            if (!paths.Add(target)) throw new InvalidDataException("整合包清单列出了重复的下载路径。");
            var hashes = item.GetProperty("hashes");
            var sha1 = hashes.GetProperty("sha1").GetString();
            var sha512 = hashes.GetProperty("sha512").GetString();
            var bytes = item.GetProperty("fileSize").GetInt64();
            if (!ModpackPaths.IsHex(sha1, 40) || !ModpackPaths.IsHex(sha512, 128) || bytes is < 0 or > MaximumFileBytes)
                throw new InvalidDataException("整合包中的文件大小或校验值无效。");
            var downloads = item.GetProperty("downloads");
            if (downloads.GetArrayLength() is < 1 or > 16) throw new InvalidDataException("整合包的下载地址数量无效。");
            var urls = downloads.EnumerateArray().Select(e => AllowedPackUri(e.GetString())).OfType<Uri>().Distinct().ToArray();
            if (urls.Length == 0) throw new NotSupportedException($"整合包中的 {Path.GetFileName(target)} 没有可用的受支持下载地址。");
            downloadBytes += bytes;
            if (downloadBytes > MaximumBytes) throw new InvalidDataException("整合包下载大小超过 32 GB。");
            files.Add(new(target, urls, sha1!, sha512!, bytes, client == "optional"));
        }

        var overrides = ReadOverrides(zip, prefix, "overrides/", true, downloadBytes, cancellation);
        CheckPaths(files.Select(file => file.Path), overrides);
        var preview = new ModpackPreview(path, hash, name, version, minecraft!, fabric,
            files.Count(f => !f.Optional), files.Count(f => f.Optional), downloadBytes);
        return new(preview, files.ToArray(), overrides);
    }

    private static ModpackArchive ReadCurseForge(ZipArchive zip, ZipArchiveEntry index, string path, string hash, CancellationToken ct)
    {
        ModpackPaths.Relative(index.FullName);
        EnsureRegularFile(index);
        if (index.Length is < 1 or > 8 * 1024 * 1024) throw new InvalidDataException("CurseForge 清单大小无效。");
        using var input = index.Open();
        using var doc = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.GetProperty("manifestType").GetString() != "minecraftModpack" || root.GetProperty("manifestVersion").GetInt32() != 1)
            throw new NotSupportedException("此 ZIP 不是受支持的 CurseForge Minecraft 客户端整合包。");
        var game = root.GetProperty("minecraft");
        var minecraft = game.GetProperty("version").GetString();
        if (!ModpackPaths.IsGameVersion(minecraft)) throw new NotSupportedException("此 CurseForge 整合包的 Minecraft 版本暂不支持。");
        var loaders = game.GetProperty("modLoaders").EnumerateArray().ToArray();
        string? fabric = null, forge = null;
        if (loaders.Length > 1) throw new NotSupportedException("当前只支持声明一种加载器的 CurseForge 整合包。");
        if (loaders is [var loader])
        {
            var id = loader.GetProperty("id").GetString() ?? "";
            if (id.StartsWith("fabric-", StringComparison.Ordinal) && ModpackPaths.IsLoaderVersion(id[7..])) fabric = id[7..];
            else if (id.StartsWith("forge-", StringComparison.Ordinal) && ModpackPaths.IsForgeVersion(minecraft, id[6..])) forge = id[6..];
            else throw new NotSupportedException($"当前不支持此整合包要求的加载器（{id}）。");
        }
        var name = Label(root, "name");
        var version = root.TryGetProperty("version", out var value) ? value.GetString()?.Trim() : null;
        // Some exporters leave version empty. The ZIP digest gives such exports a stable identity.
        version = string.IsNullOrEmpty(version) ? $"ZIP-{hash[..12]}" : Label(root, "version");
        var declarations = root.GetProperty("files");
        if (declarations.GetArrayLength() > 20000) throw new InvalidDataException("整合包要求下载的文件过多。");
        var ids = new HashSet<int>();
        var files = new List<CurseForgeFile>();
        foreach (var item in declarations.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var project = item.GetProperty("projectID").GetInt32();
            var file = item.GetProperty("fileID").GetInt32();
            if (project <= 0 || file <= 0 || !ids.Add(file)) throw new InvalidDataException("CurseForge 清单包含无效或重复的文件编号。");
            files.Add(new(project, file, !item.GetProperty("required").GetBoolean()));
        }
        var folder = ModpackPaths.Relative(root.GetProperty("overrides").GetString() ?? "");
        var overrides = ReadOverrides(zip, index.FullName[..^"manifest.json".Length], folder + '/', false, 0, ct);
        CheckPaths([], overrides);
        return new(new(path, hash, name, version, minecraft!, fabric, files.Count(f => !f.Optional),
            files.Count(f => f.Optional), 0, forge, true), [], overrides, files.ToArray());
    }

    private static PackOverride[] ReadOverrides(ZipArchive zip, string prefix, string folder, bool hasClientLayer,
        long downloadBytes, CancellationToken cancellation)
    {
        var general = new Dictionary<string, PackOverride>(StringComparer.OrdinalIgnoreCase);
        var clientOnly = new Dictionary<string, PackOverride>(StringComparer.OrdinalIgnoreCase);
        long overrideBytes = 0;
        foreach (var entry in zip.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName[prefix.Length..];
            Dictionary<string, PackOverride> layer;
            if (relative.StartsWith(folder, StringComparison.Ordinal))
            {
                layer = general;
                relative = relative[folder.Length..];
            }
            else if (hasClientLayer && relative.StartsWith("client-overrides/", StringComparison.Ordinal))
            {
                layer = clientOnly;
                relative = relative["client-overrides/".Length..];
            }
            else continue;
            relative = ModpackPaths.Relative(relative);
            EnsureRegularFile(entry);
            if (entry.Length is < 0 or > MaximumFileBytes) throw new InvalidDataException("整合包中单个文件的大小超过 2 GB。");
            overrideBytes += entry.Length;
            if (overrideBytes + downloadBytes > MaximumBytes) throw new InvalidDataException("整合包展开大小超过 32 GB。");
            if (!layer.TryAdd(relative, new(relative, entry))) throw new InvalidDataException("整合包配置包含重复路径。");
        }
        foreach (var (key, value) in clientOnly) general[key] = value;
        return general.Values.ToArray();
    }

    internal static void CheckPaths(IEnumerable<string> downloads, PackOverride[] overrides)
    {
        var paths = overrides.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in downloads)
        {
            if (!seen.Add(file)) throw new InvalidDataException("整合包清单列出了重复的下载路径。");
            paths.Add(file);
        }
        foreach (var relative in paths)
        {
            for (var separator = relative.LastIndexOf('/'); separator >= 0; separator = relative.LastIndexOf('/', separator - 1))
            {
                var parent = relative[..separator];
                if (paths.Contains(parent)) throw new InvalidDataException("整合包中的文件与文件夹使用了相同路径。");
                if (separator == 0) break;
            }
        }
    }

    private static string Label(JsonElement root, string property)
    {
        var value = root.GetProperty(property).GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value.Any(char.IsControl))
            throw new InvalidDataException("整合包清单的名称或版本号无效。");
        return value;
    }

    private static string DisplayDependency(string key) => key switch
    {
        "forge" => "Forge", "neoforge" => "NeoForge", "quilt-loader" => "Quilt", _ => "暂未支持的加载器"
    };

    private static bool IsIndex(string path, string name) => path == name
        || (path.EndsWith("/" + name, StringComparison.Ordinal) && path.Count(c => c == '/') == 1);

    private static void EnsureRegularFile(ZipArchiveEntry entry)
    {
        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("整合包含有链接，无法安全导入。");
    }

    internal static Uri? AllowedPackUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !DownloadHosts.Contains(uri.Host)) return null;
        return uri;
    }
}
