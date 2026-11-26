using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public sealed partial class ModpackImportService : IDisposable
{
    private static readonly Uri FabricMaven = new("https://maven.fabricmc.net/");
    private readonly MinecraftDownloadService _minecraft;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ModpackImportService(MinecraftDownloadService minecraft, HttpClient? http = null)
    {
        _minecraft = minecraft;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static Task<ModpackPreview> PreviewAsync(string path, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            path = Path.GetFullPath(path);
            using var file = OpenArchive(path);
            var hash = await HashArchiveAsync(file, ct).ConfigureAwait(false);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            return ModpackArchive.Read(archive, path, hash, ct).Preview;
        }, ct);

    public Task<ModpackInstallResult> InstallAsync(ModpackPreview preview, string gameDirectory, bool includeOptional,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default, string? javaPath = null,
        string? localFilesDirectory = null) =>
        Task.Run(() => InstallCoreAsync(preview, gameDirectory, includeOptional, progress, ct, javaPath, localFilesDirectory), ct);

    private async Task<ModpackInstallResult> InstallCoreAsync(ModpackPreview preview, string gameDirectory,
        bool includeOptional, IProgress<InstallProgress>? progress, CancellationToken ct, string? javaPath, string? localFilesDirectory)
    {
        Report(progress, "核对整合包", "正在确认所选文件");
        using var file = OpenArchive(preview.ArchivePath);
        var hash = await HashArchiveAsync(file, ct).ConfigureAwait(false);
        if (!string.Equals(hash, preview.ArchiveHash, StringComparison.Ordinal))
            throw new IOException("整合包文件已变化，请重新选择文件。");
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var pack = ModpackArchive.Read(zip, preview.ArchivePath, hash, ct);
        var root = Path.GetFullPath(gameDirectory);
        var stagingParent = ModpackPaths.SafeTarget(root, ".mcl-imports");
        Directory.CreateDirectory(stagingParent);
        var stagingName = Guid.NewGuid().ToString("N");
        var staging = ModpackPaths.SafeTarget(stagingParent, stagingName);
        var game = ModpackPaths.SafeTarget(staging, "game");
        Directory.CreateDirectory(game);
        var published = false;

        try
        {
            var plan = pack.Preview.CurseForge
                ? await ResolveCurseForgeFilesAsync(pack, includeOptional, localFilesDirectory, progress, ct).ConfigureAwait(false)
                : pack.Files.Where(f => !f.Optional || includeOptional).ToArray();
            Report(progress, "准备游戏版本", $"Minecraft {pack.Preview.Minecraft}");
            var versions = await _minecraft.GetVersionsAsync(ct).ConfigureAwait(false);
            var version = versions.FirstOrDefault(v => v.Id == pack.Preview.Minecraft)
                ?? throw new NotSupportedException("这个整合包需要的 Minecraft 版本不在当前可安装的官方正式版清单中。");
            var baseInfo = await _minecraft.InstallModpackBaseAsync(version, root, progress, ct).ConfigureAwait(false);
            var fabric = await InstallFabricAsync(pack.Preview.Minecraft, pack.Preview.Fabric, staging, progress, ct).ConfigureAwait(false);
            var forge = pack.Preview.Forge;
            var forgeHash = forge is null ? null : await InstallForgeAsync(version.Id, forge,
                ModpackPaths.BaseDirectory(version.Id, version.MetadataSha1, baseInfo.UsesCachedBase), root, staging, javaPath, progress, ct).ConfigureAwait(false);
            await DownloadPackFilesAsync(plan, game, progress, ct).ConfigureAwait(false);

            Report(progress, "写入整合包配置", "正在应用配置、资源包和存档");
            foreach (var item in pack.Overrides)
            {
                ct.ThrowIfCancellationRequested();
                var target = ModpackPaths.SafeTarget(game, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = item.Entry.Open();
                await using var output = File.Create(target);
                await CopyAsync(input, output, item.Entry.Length, item.Entry.Length, null, null, ct).ConfigureAwait(false);
            }

            var id = CreateId(pack.Preview.Name, stagingName[..8]);
            var destination = ModpackPaths.SafeTarget(root, $"versions/{id}");
            var settings = new ModpackInstance(1, pack.Preview.Name, pack.Preview.Version, version.Id, version.MetadataSha1,
                pack.Preview.Fabric, fabric.Libraries, fabric.Jvm, baseInfo.UsesCachedBase, forge, forgeHash);
            var inheritsFrom = settings.UsesCachedBase ? null : version.Id;
            var mainClass = forge is null ? fabric.MainClass : ModpackPaths.ForgeMain;
            ModpackPaths.CheckInstance(id, inheritsFrom, mainClass, settings);
            var metadata = JsonSerializer.Serialize(new
            {
                id, inheritsFrom, mainClass, mclModpack = settings
            });
            await File.WriteAllTextAsync(ModpackPaths.SafeTarget(staging, id + ".json"), metadata, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
            published = true;
            return new(id, pack.Preview.Name, baseInfo.RequiredJava);
        }
        finally
        {
            if (!published)
            {
                // This directory was created by this attempt and cannot be selected as a version.
                try
                {
                    if (Directory.Exists(ModpackPaths.SafeTarget(stagingParent, stagingName)))
                        Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal async Task<FabricProfile> InstallFabricAsync(string minecraft, string? loader,
        string staging, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (loader is null) return new(ModpackPaths.VanillaMain, [], []);
        if (!ModpackPaths.IsGameVersion(minecraft) || !ModpackPaths.IsLoaderVersion(loader))
            throw new InvalidDataException("Fabric 所需的版本编号无效。");
        Report(progress, "安装 Fabric", $"正在准备 Fabric Loader {loader}");
        var uri = new Uri($"https://meta.fabricmc.net/v2/versions/loader/{minecraft}/{loader}/profile/json");
        using var response = await GetSmallAsync(uri, 512 * 1024, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        var metadata = document.RootElement;
        var main = metadata.GetProperty("mainClass").GetString();
        if (metadata.GetProperty("inheritsFrom").GetString() != minecraft
            || metadata.GetProperty("id").GetString() != $"fabric-loader-{loader}-{minecraft}"
            || main is not (ModpackPaths.FabricMain or ModpackPaths.LegacyFabricMain))
            throw new InvalidDataException("Fabric 官方返回的版本信息与整合包不一致。");
        var arguments = metadata.GetProperty("arguments");
        var jvm = arguments.GetProperty("jvm").EnumerateArray().Select(element => element.GetString() ?? "").ToArray();
        if (jvm.Length > 3 || jvm.Any(argument => !ModpackPaths.IsAllowedJvmArgument(argument)) || arguments.GetProperty("game").GetArrayLength() != 0)
            throw new NotSupportedException("此 Fabric 版本需要 MCL 尚未支持的启动参数。");
        var libraries = new List<FabricLibrary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = metadata.GetProperty("libraries");
        if (list.GetArrayLength() is < 1 or > 64) throw new InvalidDataException("Fabric 依赖清单无效。");
        foreach (var library in list.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var coordinate = library.GetProperty("name").GetString() ?? "";
            var relative = ModpackPaths.LibraryPath(coordinate);
            if (!seen.Add(relative) || !Uri.TryCreate(library.GetProperty("url").GetString(), UriKind.Absolute, out var repository)
                || repository != FabricMaven)
                throw new InvalidDataException("Fabric 依赖清单包含不受支持的仓库或重复依赖。");
            var jar = new Uri(FabricMaven, relative);
            var sha = library.TryGetProperty("sha1", out var digest) ? digest.GetString() : null;
            if (sha is null)
            {
                using var checksum = await GetSmallAsync(new Uri(jar.AbsoluteUri + ".sha1"), 256, ct).ConfigureAwait(false);
                sha = System.Text.Encoding.ASCII.GetString(checksum.ToArray()).Trim();
            }
            var sha512 = library.TryGetProperty("sha512", out var highDigest) ? highDigest.GetString() : null;
            var size = library.TryGetProperty("size", out var length) ? length.GetInt64() : (long?)null;
            if (!ModpackPaths.IsHex(sha, 40) || (sha512 is not null && !ModpackPaths.IsHex(sha512, 128)) || size is < 1 or > 64 * 1024 * 1024)
                throw new InvalidDataException("Fabric 依赖校验信息无效。");
            var target = ModpackPaths.SafeTarget(staging, ".mcl-libraries/" + relative);
            if (!File.Exists(target) || !await VerifiedDownloader.IsValidAsync(target, sha!, size ?? new FileInfo(target).Length, ct).ConfigureAwait(false))
                await DownloadAsync([jar], target, size, 64 * 1024 * 1024, sha!, sha512, FabricUri, ct, overwrite: true).ConfigureAwait(false);
            libraries.Add(new(coordinate, sha!));
        }
        if (!libraries.Any(l => l.Coordinate == $"net.fabricmc:fabric-loader:{loader}"))
            throw new InvalidDataException("Fabric 依赖中没有整合包要求的加载器文件。");
        return new(main, libraries.ToArray(), jvm);
    }

    private async Task DownloadPackFilesAsync(PackFile[] files, string root, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var total = files.Sum(f => f.Bytes);
        var completed = 0;
        long bytes = 0;
        var progressGate = new object();
        long lastReport = -200;
        Report(progress, "下载整合包文件", $"共 {files.Length} 个文件");
        await Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 6 }, async (file, token) =>
        {
            var target = ModpackPaths.SafeTarget(root, file.Path);
            if (file.LocalSource is { } local)
                await CopyLocalPackFileAsync(local, target, file, token).ConfigureAwait(false);
            else if (file.Sha512 is null)
                await DownloadCurseForgePackFileAsync(file, target, token).ConfigureAwait(false);
            else
                await DownloadAsync(file.Sources, target, file.Bytes, ModpackArchive.MaximumFileBytes, file.Sha1, file.Sha512,
                    uri => ModpackArchive.AllowedPackUri(uri.AbsoluteUri) is not null, token).ConfigureAwait(false);
            lock (progressGate)
            {
                bytes += file.Bytes;
                completed++;
                if (completed != files.Length && watch.ElapsedMilliseconds - lastReport < 150) return;
                lastReport = watch.ElapsedMilliseconds;
                progress?.Report(new("下载整合包文件", Path.GetFileName(file.Path), completed, files.Length,
                    bytes, total, bytes / Math.Max(1, watch.Elapsed.TotalSeconds)));
            }
        }).ConfigureAwait(false);
    }

    private static async Task CopyLocalPackFileAsync(string source, string target, PackFile file, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(source);
            await using var output = File.Create(target);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            await CopyAsync(input, output, file.Bytes, file.Bytes, hash, null, ct).ConfigureAwait(false);
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("文件校验值不一致。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new IOException($"无法使用补充文件 {Path.GetFileName(target)}。请确认它是清单指定的原版本。", ex);
        }
    }

    internal async Task DownloadAsync(IEnumerable<Uri> sources, string target, long? size, long maxBytes,
        string sha1, string? sha512, Func<Uri, bool> allowed, CancellationToken ct, bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Exception? lastError = null;
        foreach (var source in sources)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMinutes(4));
                    using var response = await RequestAsync(source, allowed, timeout.Token).ConfigureAwait(false);
                    var reportedSize = response.Content.Headers.ContentLength;
                    if (reportedSize > maxBytes || (size.HasValue && reportedSize.HasValue && reportedSize != size))
                        throw new InvalidDataException("服务器文件大小与清单不一致。");
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var hash1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    using var hash512 = sha512 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                        await CopyAsync(input, output, maxBytes, size, hash1, hash512, timeout.Token).ConfigureAwait(false);
                    if (!Convert.ToHexString(hash1.GetHashAndReset()).Equals(sha1, StringComparison.OrdinalIgnoreCase)
                        || (hash512 is not null && !Convert.ToHexString(hash512.GetHashAndReset()).Equals(sha512, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("下载文件与清单中的校验值不一致。");
                    ct.ThrowIfCancellationRequested();
                    File.Move(temporary, target, overwrite);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException))
                {
                    lastError = ex;
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                }
            }
        }
        throw new IOException($"无法下载或验证 {Path.GetFileName(target)}。请检查网络和整合包版本后重试。", lastError);
    }

    private async Task<MemoryStream> GetSmallAsync(Uri uri, int maxBytes, CancellationToken ct, Func<Uri, bool>? allowed = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await RequestAsync(uri, allowed ?? FabricUri, timeout.Token).ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var buffer = new MemoryStream();
        try
        {
            await CopyAsync(input, buffer, maxBytes, null, null, null, timeout.Token).ConfigureAwait(false);
            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> RequestAsync(Uri uri, Func<Uri, bool> allowed, CancellationToken ct)
    {
        for (var hops = 0; hops <= 5; hops++)
        {
            if (!allowed(uri)) throw new InvalidDataException("整合包下载尝试跳转到不支持的地址。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MCL/0.5");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                    || response.Headers.Location is not { } next)
                    response.EnsureSuccessStatusCode();
                else uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
            }
        }
        throw new IOException("整合包下载的跳转次数过多。");
    }

    internal static async Task CopyAsync(Stream input, Stream output, long maxBytes, long? size,
        IncrementalHash? hash, IncrementalHash? secondaryHash, CancellationToken ct)
    {
        var buffer = new byte[65536];
        long written = 0;
        var limit = Math.Min(maxBytes, size ?? maxBytes);
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            written += read;
            if (written > limit) throw new InvalidDataException("文件大小超过清单中的限制。");
            hash?.AppendData(buffer, 0, read);
            secondaryHash?.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        if (size is not null && written != size) throw new InvalidDataException("文件大小与清单中的记录不一致。");
    }

    internal static FileStream OpenArchive(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("请选择 Modrinth .mrpack 或 CurseForge ZIP 客户端整合包。");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (stream.Length <= 8L * 1024 * 1024 * 1024) return stream;
        stream.Dispose();
        throw new InvalidDataException("整合包压缩文件超过 8 GB。");
    }

    private static async Task<string> HashArchiveAsync(FileStream stream, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexString(hash);
    }

    private static bool FabricUri(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0 && uri.Host is "meta.fabricmc.net" or "maven.fabricmc.net";

    private static string CreateId(string name, string suffix)
    {
        var baseName = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(24).ToArray());
        return $"pack-{(baseName.Length == 0 ? "modrinth" : baseName)}-{suffix}";
    }

    private static void Report(IProgress<InstallProgress>? progress, string stage, string detail) =>
        progress?.Report(new(stage, detail, 0, 0, 0, 0, 0));

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
