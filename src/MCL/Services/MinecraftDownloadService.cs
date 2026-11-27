using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public sealed class MinecraftDownloadService : IDisposable
{
    public static readonly Uri ManifestUri = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private static readonly Version EarliestRelease = new(1, 7, 10);
    private static readonly DateTimeOffset EarliestSnapshot = new(2018, 7, 18, 0, 0, 0, TimeSpan.Zero);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly VerifiedDownloader _downloader;
    private readonly SemaphoreSlim _installGate = new(1);
    private readonly Dictionary<string, VersionInstallInfo> _details = new();

    public MinecraftDownloadService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20), AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 8 }) { Timeout = Timeout.InfiniteTimeSpan };
        if (_ownsClient) _client.DefaultRequestHeaders.UserAgent.ParseAdd("MCL/0.2");
        _downloader = new VerifiedDownloader(_client);
    }

    public async Task<IReadOnlyList<RemoteGameVersion>> GetVersionsAsync(CancellationToken ct = default, bool includeSnapshots = false)
        => ParseManifest(await _downloader.GetBytesAsync(ManifestUri, 8 * 1024 * 1024, null, ct), includeSnapshots);

    public static IReadOnlyList<RemoteGameVersion> ParseManifest(byte[] bytes, bool includeSnapshots = false)
    {
        using var doc = JsonDocument.Parse(bytes);
        var versions = new List<RemoteGameVersion>();
        foreach (var item in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            var type = item.GetProperty("type").GetString();
            if (type != "release" && !(includeSnapshots && type == "snapshot")) continue;
            var id = item.GetProperty("id").GetString()!;
            if (!VersionCatalog.IsSafeVersionId(id)) continue;
            var released = item.GetProperty("releaseTime").GetDateTimeOffset();
            if (type == "release")
            {
                if (!Version.TryParse(id, out var numeric) || numeric < EarliestRelease) continue;
            }
            // This cutoff keeps the snapshot list on the modern asset layout.
            else if (released < EarliestSnapshot) continue;
            var sha1 = item.GetProperty("sha1").GetString()!;
            if (!VerifiedDownloader.ValidSha1(sha1)) throw new InvalidDataException("版本清单校验信息不正确。");
            versions.Add(new RemoteGameVersion(id, type!, released, VerifiedDownloader.CheckedUri(item.GetProperty("url").GetString()!), sha1));
        }
        return versions.OrderByDescending(item => item.ReleaseTime).DistinctBy(item => item.Id).ToArray();
    }

    public async Task<VersionInstallInfo> InspectVersionAsync(RemoteGameVersion version, CancellationToken ct = default)
    {
        lock (_details) { if (_details.TryGetValue(version.MetadataSha1, out var cached)) return cached; }
        var bytes = await _downloader.GetBytesAsync(version.MetadataUrl, 16 * 1024 * 1024, version.MetadataSha1, ct);
        var info = ParseVersionInfo(version, bytes);
        lock (_details) _details[version.MetadataSha1] = info;
        return info;
    }

    internal static VersionInstallInfo ParseVersionInfo(RemoteGameVersion version, byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (version.Type is not ("release" or "snapshot") || root.GetProperty("id").GetString() != version.Id
            || root.GetProperty("type").GetString() != version.Type || root.TryGetProperty("inheritsFrom", out _)
            || root.GetProperty("mainClass").GetString() != "net.minecraft.client.main.Main")
            throw new NotSupportedException("这个版本的启动方式暂未支持，请选择其他版本。");
        var java = MinecraftMetadata.RequiredJava(root);
        var estimate = root.GetProperty("downloads").GetProperty("client").GetProperty("size").GetInt64();
        if (root.TryGetProperty("assetIndex", out var index) && index.TryGetProperty("totalSize", out var total)) estimate += total.GetInt64();
        var estimatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtime = new JavaInstallation("", java, "", true);
        foreach (var library in root.GetProperty("libraries").EnumerateArray())
        {
            if (!MinecraftMetadata.RulesAllow(library, runtime) || !library.TryGetProperty("downloads", out var downloads)) continue;
            void Count(JsonElement file)
            {
                var key = file.TryGetProperty("path", out var path) ? path.GetString()! : file.GetProperty("url").GetString()!;
                if (estimatedPaths.Add(key)) estimate += file.GetProperty("size").GetInt64();
            }
            if (downloads.TryGetProperty("artifact", out var artifact)) Count(artifact);
            if (library.TryGetProperty("natives", out var natives) && natives.TryGetProperty("windows", out var native)
                && downloads.TryGetProperty("classifiers", out var classifiers)
                && classifiers.TryGetProperty(native.GetString()!.Replace("${arch}", "64", StringComparison.Ordinal), out var nativeFile)) Count(nativeFile);
        }
        return new VersionInstallInfo(version, java, estimate, bytes);
    }

    public Task<InstallResult> InstallAsync(RemoteGameVersion version, string gameDirectory, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        => InstallAsync(version, gameDirectory, progress, false, ct);

    internal Task<InstallResult> InstallModpackBaseAsync(RemoteGameVersion version, string gameDirectory, IProgress<InstallProgress>? progress, CancellationToken ct)
        => InstallAsync(version, gameDirectory, progress, true, ct);

    internal async Task<InstallResult> RepairAsync(GameVersion installed, string gameDirectory, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var path = SafeTarget(gameDirectory, $"versions/{installed.Id}/{installed.Id}.json");
        if (!string.Equals(path, Path.GetFullPath(installed.JsonPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("所选版本不在当前游戏目录中。");
        ModpackInstance? pack = null;
        if (installed.ModpackName is not null)
        {
            using var stream = File.OpenRead(path);
            if (stream.Length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("整合包配置无法读取，请重新导入整合包。");
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var metadata = document.RootElement;
            if (!metadata.TryGetProperty(ModpackPaths.InstanceKey, out var instance) || instance.Deserialize<ModpackInstance>() is not { } found)
                throw new InvalidDataException("整合包配置无法读取，请重新导入整合包。");
            ModpackPaths.CheckInstance(installed.Id,
                metadata.TryGetProperty("inheritsFrom", out var parent) ? parent.GetString() : null,
                metadata.GetProperty("mainClass").GetString(), found);
            pack = found;
        }
        else if (installed.Kind != "原版" || installed.InheritsFrom is not null)
            throw new NotSupportedException("这里可以修复原版和 MCL 整合包使用的官方基础文件。");

        progress?.Report(new("读取官方版本列表", pack?.Minecraft ?? installed.Id, 0, 0, 0, 0, 0));
        var versions = await GetVersionsAsync(ct, includeSnapshots: true);
        var version = versions.FirstOrDefault(item => item.Id == (pack?.Minecraft ?? installed.Id));
        if (version is null || pack is not null && !string.Equals(version.MetadataSha1, pack.BaseSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("官方清单中找不到相符的版本文件，暂时无法修复。");
        return await InstallAsync(version, gameDirectory, progress, false, ct, new RepairTarget(pack?.UsesCachedBase == true));
    }

    private sealed record RepairTarget(bool CachedBase);

    private async Task<InstallResult> InstallAsync(RemoteGameVersion version, string gameDirectory, IProgress<InstallProgress>? progress,
        bool allowCachedBase, CancellationToken ct, RepairTarget? repair = null)
    {
        if (!await _installGate.WaitAsync(0, ct)) throw new InvalidOperationException("已有一个版本正在安装。");
        try { return await InstallCoreAsync(version, gameDirectory, progress, allowCachedBase, ct, repair); }
        catch (HttpRequestException ex) { throw new IOException("无法连接官方文件服务器，请检查网络后重新尝试。", ex); }
        catch (UnauthorizedAccessException ex) { throw new IOException("无法写入游戏目录，请选择当前账户可以写入的文件夹。", ex); }
        finally { _installGate.Release(); }
    }

    private async Task<InstallResult> InstallCoreAsync(RemoteGameVersion version, string gameDirectory, IProgress<InstallProgress>? progress,
        bool allowCachedBase, CancellationToken ct, RepairTarget? repair)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) throw new InvalidDataException("请先选择游戏安装文件夹。");
        var root = Path.GetFullPath(gameDirectory);
        CheckLinks(root);
        progress?.Report(new("读取版本信息", version.Id, 0, 0, 0, 0, 0));
        var info = await InspectVersionAsync(version, ct);
        using var metadataDoc = JsonDocument.Parse(info.Metadata);
        var metadata = metadataDoc.RootElement;
        var lockPath = SafeTarget(root, $".mcl-downloads/{version.Id}.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var installationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var versionBase = ModpackPaths.BaseDirectory(version.Id, version.MetadataSha1, repair?.CachedBase == true);
        var versionJson = SafeTarget(root, $"{versionBase}/{version.Id}.json");
        var hasMetadata = File.Exists(versionJson);
        var validMetadata = hasMetadata && await VerifiedDownloader.IsValidAsync(versionJson, version.MetadataSha1, info.Metadata.Length, ct);
        var useCachedBase = repair?.CachedBase == true;
        if (hasMetadata && !validMetadata && !useCachedBase)
        {
            if (repair is not null) throw new IOException("本地版本配置与官方原件不同，已保留。请通过原安装来源处理版本配置。");
            if (!allowCachedBase) throw new IOException("已存在同名版本，且配置与官方版本不同。为保护已有配置，请换一个游戏目录安装。");
            useCachedBase = true;
            progress?.Report(new("准备游戏版本", "保留同名版本，使用整合包专用的官方游戏文件", 0, 0, 0, 0, 0));
            versionBase = ModpackPaths.BaseDirectory(version.Id, version.MetadataSha1, true);
            versionJson = SafeTarget(root, $"{versionBase}/{version.Id}.json");
        }
        var plan = new Dictionary<string, DownloadFile>(StringComparer.OrdinalIgnoreCase);
        void Add(JsonElement descriptor, string relativePath)
        {
            var sha1 = descriptor.GetProperty("sha1").GetString()!;
            var size = descriptor.GetProperty("size").GetInt64();
            if (!VerifiedDownloader.ValidSha1(sha1) || size < 0) throw new InvalidDataException("文件清单的校验信息不正确。");
            var file = new DownloadFile(VerifiedDownloader.CheckedUri(descriptor.GetProperty("url").GetString()!), SafeTarget(root, relativePath), sha1, size);
            if (plan.TryGetValue(file.Path, out var previous) && (previous.Sha1 != file.Sha1 || previous.Size != file.Size)) throw new InvalidDataException("文件清单包含冲突的下载路径。");
            plan[file.Path] = file;
        }
        Add(metadata.GetProperty("downloads").GetProperty("client"), $"{versionBase}/{version.Id}.jar");
        var java = new JavaInstallation("", info.RequiredJava, "", true);
        foreach (var library in metadata.GetProperty("libraries").EnumerateArray())
        {
            if (!MinecraftMetadata.RulesAllow(library, java)) continue;
            var name = library.GetProperty("name").GetString()!;
            if (!library.TryGetProperty("downloads", out var downloads)) throw new NotSupportedException("这个版本使用了较旧的依赖格式，暂不支持自动安装。");
            if (downloads.TryGetProperty("artifact", out var artifact)) Add(artifact, "libraries/" + (artifact.TryGetProperty("path", out var ap) ? ap.GetString() : MinecraftMetadata.MavenPath(name)));
            if (library.TryGetProperty("natives", out var natives) && natives.TryGetProperty("windows", out var native))
            {
                var classifier = native.GetString()!.Replace("${arch}", "64", StringComparison.Ordinal);
                var nativeFile = downloads.GetProperty("classifiers").GetProperty(classifier);
                Add(nativeFile, "libraries/" + (nativeFile.TryGetProperty("path", out var np) ? np.GetString() : MinecraftMetadata.MavenPath(name, classifier)));
            }
        }
        if (metadata.TryGetProperty("logging", out var logging) && logging.TryGetProperty("client", out var loggingClient) && loggingClient.TryGetProperty("file", out var loggingFile))
            Add(loggingFile, "assets/log_configs/" + loggingFile.GetProperty("id").GetString());

        var assetIndex = metadata.GetProperty("assetIndex");
        var indexId = assetIndex.GetProperty("id").GetString()!;
        var indexHash = assetIndex.GetProperty("sha1").GetString()!;
        var indexPath = SafeTarget(root, "assets/indexes/" + indexId + ".json");
        if (!VerifiedDownloader.ValidSha1(indexHash)) throw new InvalidDataException("资源索引校验信息不正确。");
        progress?.Report(new("读取资源清单", "正在准备声音、语言等资源", 0, 0, 0, 0, 0));
        var indexSize = assetIndex.GetProperty("size").GetInt64();
        if (indexSize is < 1 or > 64 * 1024 * 1024) throw new InvalidDataException("资源索引大小不正确。");
        var validIndex = repair is not null && await VerifiedDownloader.IsValidAsync(indexPath, indexHash, indexSize, ct);
        var indexBytes = validIndex ? await File.ReadAllBytesAsync(indexPath, ct)
            : await _downloader.GetBytesAsync(VerifiedDownloader.CheckedUri(assetIndex.GetProperty("url").GetString()!), 64 * 1024 * 1024, indexHash, ct);
        if (indexBytes.LongLength != indexSize || validIndex &&
            !Convert.ToHexString(SHA1.HashData(indexBytes)).Equals(indexHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源索引与官方清单不一致，请重新检查。");
        using var indexDoc = JsonDocument.Parse(indexBytes);
        var indexRoot = indexDoc.RootElement;
        if ((indexRoot.TryGetProperty("virtual", out var virtualFlag) && virtualFlag.ValueKind == JsonValueKind.True)
            || (indexRoot.TryGetProperty("map_to_resources", out var mapFlag) && mapFlag.ValueKind == JsonValueKind.True))
            throw new NotSupportedException("这个版本使用旧式资源目录，暂不支持自动安装。");
        foreach (var asset in indexRoot.GetProperty("objects").EnumerateObject())
        {
            var hash = asset.Value.GetProperty("hash").GetString()!;
            var size = asset.Value.GetProperty("size").GetInt64();
            if (!VerifiedDownloader.ValidSha1(hash) || size < 0) throw new InvalidDataException("资源清单包含无效文件。");
            var relative = hash[..2] + "/" + hash;
            var path = SafeTarget(root, "assets/objects/" + relative);
            if (plan.TryGetValue(path, out var old) && old.Size != size) throw new InvalidDataException("资源清单包含冲突大小。");
            plan[path] = new DownloadFile(new Uri("https://resources.download.minecraft.net/" + relative), path, hash, size);
        }
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(versionJson)!);
        var marker = SafeTarget(root, $"{versionBase}/.mcl-installing");
        if (!File.Exists(versionJson)) await File.WriteAllTextAsync(marker, version.Id, ct);
        var files = plan.Values.ToArray();
        var totalBytes = checked(files.Sum(file => file.Size));
        var pending = new System.Collections.Concurrent.ConcurrentBag<DownloadFile>();
        var activeBytes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        long completedBytes = 0, receivedBytes = 0;
        int completedFiles = 0, downloaded = 0, reused = 0;
        var stopwatch = Stopwatch.StartNew();
        long lastReport = -1000;
        var reportGate = new object();
        void Report(string stage, string detail, bool force = false)
        {
            lock (reportGate)
            {
                if (!force && stopwatch.ElapsedMilliseconds - lastReport < 150) return;
                lastReport = stopwatch.ElapsedMilliseconds;
                progress?.Report(new(stage, detail, Volatile.Read(ref completedFiles), files.Length, Math.Min(totalBytes, Volatile.Read(ref completedBytes) + activeBytes.Values.Sum()), totalBytes,
                    stopwatch.Elapsed.TotalSeconds > 0 ? Volatile.Read(ref receivedBytes) / stopwatch.Elapsed.TotalSeconds : 0));
            }
        }
        Report("校验已有文件", "完整文件将直接复用", true);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (file, token) =>
        {
            if (await VerifiedDownloader.IsValidAsync(file.Path, file.Sha1, file.Size, token))
            {
                Interlocked.Increment(ref reused); Interlocked.Increment(ref completedFiles); Interlocked.Add(ref completedBytes, file.Size);
            }
            else pending.Add(file);
            Report("校验已有文件", FriendlyFile(file));
        });
        stopwatch.Restart();
        lastReport = -1000;
        Report("下载游戏文件", $"共 {pending.Count} 个文件需要下载", true);
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (file, token) =>
        {
            SafeTarget(root, Path.GetRelativePath(root, file.Path));
            await _downloader.DownloadAsync(file, count => Interlocked.Add(ref receivedBytes, count), token,
                current => { activeBytes[file.Path] = current; Report("下载游戏文件", FriendlyFile(file)); });
            lock (reportGate)
            {
                activeBytes.TryRemove(file.Path, out _);
                Interlocked.Increment(ref downloaded); Interlocked.Increment(ref completedFiles); Interlocked.Add(ref completedBytes, file.Size);
            }
            Report("下载游戏文件", FriendlyFile(file));
        });
        ct.ThrowIfCancellationRequested();
        Report(repair is null ? "完成安装" : "完成修复", "正在整理版本文件", true);
        if (!validIndex) await WriteCompleteFileAsync(SafeTarget(root, Path.GetRelativePath(root, indexPath)), indexBytes, ct);
        if (repair is null || !validMetadata)
            await WriteCompleteFileAsync(SafeTarget(root, Path.GetRelativePath(root, versionJson)), info.Metadata, ct);
        if (repair is not null)
        {
            downloaded += (validIndex ? 0 : 1) + (validMetadata ? 0 : 1);
            reused += (validIndex ? 1 : 0) + (validMetadata ? 1 : 0);
        }
        try { if (File.Exists(marker)) File.Delete(marker); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Report(repair is null ? "安装完成" : "修复完成", $"下载 {downloaded} 个文件，复用 {reused} 个文件", true);
        return new InstallResult(version.Id, downloaded, reused, info.RequiredJava) { UsesCachedBase = useCachedBase };
    }

    private static async Task WriteCompleteFileAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".mclpart";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static string SafeTarget(string root, string relative)
    {
        if (relative.Split('/', '\\').Any(part => part is "." or "..")) throw new InvalidDataException("下载清单包含越界路径。");
        var target = MinecraftMetadata.SafePath(root, relative);
        var directory = root;
        CheckLinks(root);
        foreach (var part in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar)) { directory = Path.Combine(directory, part); CheckLinks(directory); }
        return target;
    }

    private static void CheckLinks(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("安装路径包含链接目录。请为这次安装选择一个普通文件夹。");
    }

    private static string FriendlyFile(DownloadFile file) => file.Path.Contains(Path.Combine("assets", "objects"), StringComparison.OrdinalIgnoreCase)
        ? "声音、语言等游戏资源" : Path.GetFileName(file.Path);

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
