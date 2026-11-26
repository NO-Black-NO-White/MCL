using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

internal sealed class JavaRuntimeInstaller(string dataDirectory, JavaDiscovery discovery, HttpClient? client = null)
{
    internal static readonly Uri CatalogUri = new("https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json");
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly VerifiedDownloader _downloader = new(client ?? Http);

    internal static string RuntimeRoot(string dataDirectory) => Path.Combine(dataDirectory, "java-runtimes");
    internal static string Executable(string runtimeRoot, int major) => Path.Combine(runtimeRoot, $"java-{major}", "bin", "java.exe");

    internal async Task<JavaInstallation> InstallAsync(int major, IProgress<string>? progress, CancellationToken ct)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new NotSupportedException("当前仅支持自动安装 Windows x64 Java，请在启动设置中选择已有的 Java。");

        var root = ModpackPaths.SafeTarget(dataDirectory, "java-runtimes");
        Directory.CreateDirectory(root);
        var target = ModpackPaths.SafeTarget(root, $"java-{major}");
        using var installLock = new FileStream(ModpackPaths.SafeTarget(root, $"java-{major}.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var executable = ModpackPaths.SafeTarget(target, "bin/java.exe");
        if (await discovery.InspectAsync(executable, ct).ConfigureAwait(false) is { } existing
            && JavaDiscovery.Matches(existing, major)) return existing;

        progress?.Report($"正在读取 Minecraft 官方 Java {major} 清单…");
        var catalog = await _downloader.GetBytesAsync(CatalogUri, 2 * 1024 * 1024, null, ct).ConfigureAwait(false);
        var (url, digest) = SelectManifest(catalog, major);
        var manifest = await _downloader.GetBytesAsync(url, 4 * 1024 * 1024, digest, ct).ConfigureAwait(false);

        var staging = ModpackPaths.SafeTarget(root, $".install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var files = ReadFiles(manifest, staging);
            var completed = 0;
            progress?.Report($"正在下载 Java {major} 文件（共 {files.Length} 个）…");
            await Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 8 },
                async (file, token) =>
                {
                    await _downloader.DownloadAsync(file, _ => { }, token).ConfigureAwait(false);
                    var count = Interlocked.Increment(ref completed);
                    if (count % 16 == 0 || count == files.Length)
                        progress?.Report($"正在安装 Java {major}（{count}/{files.Length} 个文件已校验）…");
                }).ConfigureAwait(false);

            var java = await discovery.InspectAsync(Path.Combine(staging, "bin", "java.exe"), ct).ConfigureAwait(false);
            if (java is null || !JavaDiscovery.Matches(java, major))
                throw new InvalidDataException($"下载的 Java 未通过运行检查，需要 64 位 Java {major}。");
            ct.ThrowIfCancellationRequested();

            var previous = target + ".previous-" + Guid.NewGuid().ToString("N");
            var replacing = Directory.Exists(target);
            if (replacing) Directory.Move(target, previous);
            try { Directory.Move(staging, target); }
            catch
            {
                if (replacing && !Directory.Exists(target)) Directory.Move(previous, target);
                throw;
            }
            if (replacing) RemoveTemporaryDirectory(previous);
            return java with { Path = executable };
        }
        finally { RemoveTemporaryDirectory(staging); }
    }

    private static (Uri Url, string Digest) SelectManifest(byte[] catalog, int major)
    {
        using var document = JsonDocument.Parse(catalog);
        var windows = document.RootElement.GetProperty("windows-x64");
        var selected = windows.EnumerateObject()
            .Where(component => component.Name == "jre-legacy" || component.Name.StartsWith("java-runtime-", StringComparison.Ordinal))
            .SelectMany(component => component.Value.EnumerateArray().Select(item => (component.Name, Item: item)))
            .Where(candidate => ReadMajor(candidate.Item.GetProperty("version").GetProperty("name").GetString()) == major)
            .OrderBy(candidate => candidate.Name.EndsWith("-snapshot", StringComparison.Ordinal))
            .ThenByDescending(candidate => candidate.Item.GetProperty("version").GetProperty("released").GetDateTimeOffset())
            .FirstOrDefault();
        if (selected.Item.ValueKind == JsonValueKind.Undefined)
            throw new NotSupportedException($"Minecraft 官方清单暂未提供 Windows x64 Java {major}。可在启动设置中指定已有的 Java。");
        var manifest = selected.Item.GetProperty("manifest");
        return (VerifiedDownloader.CheckedUri(manifest.GetProperty("url").GetString()!), CheckedDigest(manifest));
    }

    private static int ReadMajor(string? value)
    {
        var text = value ?? "";
        if (text.StartsWith("1.", StringComparison.Ordinal)) text = text[2..];
        return int.TryParse(new string(text.TakeWhile(char.IsAsciiDigit).ToArray()), out var major) ? major : 0;
    }

    private static DownloadFile[] ReadFiles(byte[] manifest, string staging)
    {
        using var document = JsonDocument.Parse(manifest);
        var files = new Dictionary<string, DownloadFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in document.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = ModpackPaths.SafeTarget(staging, item.Name);
            switch (item.Value.GetProperty("type").GetString())
            {
                case "directory":
                    continue;
                case "file":
                    var raw = item.Value.GetProperty("downloads").GetProperty("raw");
                    var size = raw.GetProperty("size").GetInt64();
                    if (files.Count >= 2500 || size < 0 || size > 256L * 1024 * 1024
                        || (total += size) > 640L * 1024 * 1024
                        || !files.TryAdd(path, new DownloadFile(VerifiedDownloader.CheckedUri(raw.GetProperty("url").GetString()!), path, CheckedDigest(raw), size)))
                        throw new InvalidDataException("官方 Java 清单的文件数量、路径或大小无效。");
                    break;
                default:
                    throw new InvalidDataException("官方 Java 清单包含当前系统不支持的文件类型。");
            }
        }
        if (!files.ContainsKey(Path.Combine(staging, "bin", "java.exe")))
            throw new InvalidDataException("官方 Java 清单缺少 java.exe。");
        return files.Values.ToArray();
    }

    private static string CheckedDigest(JsonElement item)
    {
        var digest = item.GetProperty("sha1").GetString();
        return ModpackPaths.IsHex(digest, 40) ? digest! : throw new InvalidDataException("官方 Java 清单的校验信息无效。");
    }

    private static void RemoveTemporaryDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
