using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCL.Models;

namespace MCL.Services;

public sealed class GameLaunchService(LogService log) : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private bool _starting;
    private bool _disposed;
    public bool IsRunning { get { lock (_gate) return _process is not null || _starting; } }
    public event Action<bool>? RunningChanged;

    public async Task LaunchAsync(LauncherSettings settings, GameVersion version, JavaInstallation java, CancellationToken ct = default, GameLaunchIdentity? identity = null)
    {
        if (identity is not null)
        {
            log.RegisterSecret(identity.AccessToken);
            identity.Validate();
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not null || _starting) throw new InvalidOperationException("已有一个游戏正在运行或准备启动。");
            _starting = true;
        }
        NotifyRunningChanged(true);
        string? nativeDirectory = null;
        try
        {
            var startInfo = await Task.Run(() => BuildStartInfo(settings, version, java, out nativeDirectory, ct, identity), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            identity?.Validate();
            var process = new Process { StartInfo = startInfo };
            try
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!process.Start()) throw new InvalidOperationException("Java 进程未能启动。");
                    _process = process;
                    _starting = false;
                }
            }
            catch { process.Dispose(); throw; }
            log.Info(identity is null
                ? $"已启动 {version.DisplayName}（本地档案：{settings.PlayerName}，内存 {settings.MemoryMb} MB）。"
                : $"已启动 {version.DisplayName}（正版账号：{identity.PlayerName}，内存 {settings.MemoryMb} MB）。");
            var directoryToClean = nativeDirectory;
            nativeDirectory = null;
            _ = ObserveAsync(process, directoryToClean);
        }
        catch
        {
            lock (_gate) _starting = false;
            CleanNatives(nativeDirectory);
            NotifyRunningChanged(false);
            throw;
        }
    }

    private static ProcessStartInfo BuildStartInfo(LauncherSettings settings, GameVersion version, JavaInstallation java, out string? nativeDirectory, CancellationToken ct, GameLaunchIdentity? identity = null)
    {
        nativeDirectory = null;
        ct.ThrowIfCancellationRequested();
        if (!version.IsValid) throw new InvalidDataException(version.Issue ?? "所选版本的配置文件不完整。");
        if (!string.IsNullOrWhiteSpace(version.InheritsFrom) && version.ModpackName is null)
            throw new NotSupportedException("当前支持独立原版和由 MCL 导入的整合包。其他启动器的继承版本暂不支持。");
        identity?.Validate();
        if (identity is null && !Regex.IsMatch(settings.PlayerName, "^[A-Za-z0-9_]{1,16}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("本地玩家名称需为 1–16 位英文字母、数字或下划线。");
        if (settings.MemoryMb is < 512 or > 65536) throw new InvalidDataException("游戏内存需要在 512–65536 MB 之间。");
        if (!java.Is64Bit && settings.MemoryMb > 1536) throw new InvalidDataException("当前 Java 为 32 位，请使用 64 位 Java，或把内存调低至 1536 MB 以内。");
        if (!File.Exists(java.Path)) throw new FileNotFoundException("找不到所选 Java，请在设置中重新选择。", java.Path);
        var gameDirectory = Path.GetFullPath(settings.GameDirectory);
        if (!Directory.Exists(gameDirectory)) throw new DirectoryNotFoundException("找不到游戏文件夹，请先在设置中选择 .minecraft 文件夹。");
        // Keep all metadata paths inside the selected game directory.
        var expectedJson = MinecraftMetadata.SafePath(Path.Combine(gameDirectory, "versions"), version.Id + "/" + version.Id + ".json");
        if (!string.Equals(expectedJson, Path.GetFullPath(version.JsonPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("版本配置路径与所选游戏文件夹不一致，请重新扫描版本。");
        if (version.ModpackName is not null) ModpackPaths.SafeTarget(gameDirectory, $"versions/{version.Id}/{version.Id}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(expectedJson), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 });
        var metadata = document.RootElement;
        var mainClass = metadata.GetProperty("mainClass").GetString();
        ModpackInstance? pack = null;
        JsonDocument? baseMetadata = null;
        var runtimeDirectory = gameDirectory;
        var versionJar = MinecraftMetadata.SafePath(Path.Combine(gameDirectory, "versions"), version.Id + "/" + version.Id + ".jar");
        if (metadata.TryGetProperty(ModpackPaths.InstanceKey, out var packJson))
        {
            pack = packJson.Deserialize<ModpackInstance>() ?? throw new InvalidDataException("整合包实例配置无效。");
            ModpackPaths.CheckInstance(version.Id, metadata.GetProperty("inheritsFrom").GetString(), mainClass, pack);
            runtimeDirectory = ModpackPaths.SafeTarget(gameDirectory, $"versions/{version.Id}/game");
            if (!Directory.Exists(runtimeDirectory)) throw new DirectoryNotFoundException("整合包的游戏目录不存在，请重新导入。");
            var versionBase = ModpackPaths.BaseDirectory(pack.Minecraft, pack.BaseSha1, pack.UsesCachedBase);
            versionJar = ModpackPaths.SafeTarget(gameDirectory, $"{versionBase}/{pack.Minecraft}.jar");
            var basePath = ModpackPaths.SafeTarget(gameDirectory, $"{versionBase}/{pack.Minecraft}.json");
            using var input = File.OpenRead(basePath);
            if (input.Length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("整合包的基础版本配置有误。");
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            if (!Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).Equals(pack.BaseSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("整合包的原版配置与安装时的官方清单不一致，请修复基础游戏版本。");
            baseMetadata = JsonDocument.Parse(bytes);
            metadata = baseMetadata.RootElement;
        }
        else
        {
            if (metadata.TryGetProperty("inheritsFrom", out _) || metadata.TryGetProperty("jar", out _))
                throw new NotSupportedException("当前支持独立原版和由 MCL 导入的整合包。");
            if (mainClass is not ("net.minecraft.client.main.Main" or "net.minecraft.client.Minecraft"))
                throw new NotSupportedException("这个版本的启动方式暂不支持。");
        }
        using var baseDocument = baseMetadata;
        if (metadata.TryGetProperty("javaVersion", out var javaRequirement) && javaRequirement.TryGetProperty("majorVersion", out var major))
        {
            var required = major.GetInt32();
            if (java.MajorVersion < required) throw new InvalidDataException($"这个游戏版本至少需要 Java {required}，当前选择的是 Java {java.MajorVersion}。");
        }
        if (!File.Exists(versionJar)) throw new FileNotFoundException("缺少游戏主文件，请先用已有启动器补全这个版本。", versionJar);
        var assetsDirectory = Path.Combine(gameDirectory, "assets");
        MinecraftMetadata.ValidateAssets(metadata, assetsDirectory, ct);

        var libraryRoot = Path.Combine(gameDirectory, "libraries");
        var classpath = new List<string>();
        if (pack is not null)
        {
            foreach (var library in pack.Libraries)
            {
                ct.ThrowIfCancellationRequested();
                var path = ModpackPaths.SafeTarget(gameDirectory, $"versions/{version.Id}/.mcl-libraries/{ModpackPaths.LibraryPath(library.Coordinate)}");
                using var file = File.OpenRead(path);
                if (file.Length is < 1 or > 64 * 1024 * 1024
                    || !Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(file)).Equals(library.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Fabric 依赖损坏，请重新导入这个整合包。");
                classpath.Add(path);
            }
        }
        var nativeArchives = new List<(string Path, List<string> Exclusions)>();
        if (metadata.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (!MinecraftMetadata.RulesAllow(library, java)) continue;
                var name = library.GetProperty("name").GetString() ?? throw new InvalidDataException("游戏依赖缺少名称。");
                JsonElement downloads = default;
                var hasDownloads = library.TryGetProperty("downloads", out downloads);
                string? artifactPath = null;
                if (hasDownloads && downloads.TryGetProperty("artifact", out var artifact)) artifactPath = artifact.TryGetProperty("path", out var path) ? path.GetString() : MinecraftMetadata.MavenPath(name);
                else if (!hasDownloads) artifactPath = MinecraftMetadata.MavenPath(name);
                if (artifactPath is not null)
                {
                    var fullPath = MinecraftMetadata.SafePath(libraryRoot, artifactPath);
                    if (!File.Exists(fullPath)) throw new FileNotFoundException($"缺少游戏依赖 {name}。请先用已有启动器补全文件。", fullPath);
                    classpath.Add(fullPath);
                }
                if (library.TryGetProperty("natives", out var natives) && natives.TryGetProperty("windows", out var windowsNative))
                {
                    var classifier = (windowsNative.GetString() ?? "").Replace("${arch}", java.Is64Bit ? "64" : "32", StringComparison.Ordinal);
                    string nativeRelativePath;
                    if (hasDownloads && downloads.TryGetProperty("classifiers", out var classifiers) && classifiers.TryGetProperty(classifier, out var classifierArtifact))
                        nativeRelativePath = classifierArtifact.TryGetProperty("path", out var path) ? path.GetString()! : MinecraftMetadata.MavenPath(name, classifier);
                    else nativeRelativePath = MinecraftMetadata.MavenPath(name, classifier);
                    var fullPath = MinecraftMetadata.SafePath(libraryRoot, nativeRelativePath);
                    if (!File.Exists(fullPath)) throw new FileNotFoundException($"缺少 Windows 原生库 {name}，请先补全游戏文件。", fullPath);
                    var exclusions = new List<string>();
                    if (library.TryGetProperty("extract", out var extract) && extract.TryGetProperty("exclude", out var exclude))
                        exclusions.AddRange(exclude.EnumerateArray().Select(item => item.GetString() ?? ""));
                    nativeArchives.Add((fullPath, exclusions));
                }
            }
        }
        classpath.Add(versionJar);
        nativeDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCL", "natives", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nativeDirectory);
        foreach (var archive in nativeArchives) MinecraftMetadata.ExtractNatives(archive.Path, nativeDirectory, archive.Exclusions, ct);

        var assetsId = metadata.TryGetProperty("assetIndex", out var assetIndex) ? assetIndex.GetProperty("id").GetString()! : metadata.GetProperty("assets").GetString()!;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = identity?.PlayerName ?? settings.PlayerName,
            ["version_name"] = version.Id,
            ["game_directory"] = runtimeDirectory,
            ["assets_root"] = assetsDirectory,
            ["assets_index_name"] = assetsId,
            ["auth_uuid"] = identity?.ProfileId ?? MinecraftMetadata.OfflineUuid(settings.PlayerName),
            ["auth_access_token"] = identity?.AccessToken ?? "0",
            ["auth_session"] = identity is null ? "0" : $"token:{identity.AccessToken}:{identity.ProfileId}",
            ["auth_xuid"] = identity?.XboxUserId ?? "",
            ["clientid"] = identity?.ClientId ?? "",
            ["user_type"] = identity is null ? "legacy" : "msa",
            ["user_properties"] = "{}",
            ["version_type"] = metadata.TryGetProperty("type", out var type) ? type.GetString() ?? "release" : "release",
            ["natives_directory"] = nativeDirectory,
            ["launcher_name"] = "MCL",
            ["launcher_version"] = "0.5.0",
            ["classpath"] = string.Join(Path.PathSeparator, classpath),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = libraryRoot,
            ["game_assets"] = Path.Combine(assetsDirectory, "virtual", assetsId),
            ["resolution_width"] = "854",
            ["resolution_height"] = "480"
        };
        var info = new ProcessStartInfo(java.Path)
        {
            WorkingDirectory = runtimeDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        var jvmArguments = new List<string>();
        var gameArguments = new List<string>();
        if (metadata.TryGetProperty("arguments", out var modern))
        {
            if (modern.TryGetProperty("jvm", out var jvm)) jvmArguments.AddRange(MinecraftMetadata.ReadArguments(jvm, java));
            if (!modern.TryGetProperty("game", out var game)) throw new InvalidDataException("版本缺少游戏启动参数。");
            gameArguments.AddRange(MinecraftMetadata.ReadArguments(game, java));
        }
        else if (metadata.TryGetProperty("minecraftArguments", out var legacy)) gameArguments.AddRange(MinecraftMetadata.SplitLegacyArguments(legacy.GetString() ?? ""));
        else throw new InvalidDataException("版本缺少可识别的游戏启动参数。");
        if (!jvmArguments.Any(argument => argument.StartsWith("-Djava.library.path=", StringComparison.Ordinal))) jvmArguments.Add("-Djava.library.path=${natives_directory}");
        if (!jvmArguments.Any(argument => argument is "-cp" or "-classpath" or "--class-path")) jvmArguments.AddRange(["-cp", "${classpath}"]);
        if (pack is not null) jvmArguments.AddRange(pack.JvmArguments);
        // The settings value wins over any memory values bundled with local metadata.
        foreach (var argument in jvmArguments.Where(argument => !argument.StartsWith("-Xmx", StringComparison.Ordinal) && !argument.StartsWith("-Xms", StringComparison.Ordinal)))
            info.ArgumentList.Add(MinecraftMetadata.Expand(argument, replacements));
        info.ArgumentList.Add("-Xmx" + settings.MemoryMb + "M");
        // Java otherwise uses the Windows locale for redirected streams on some JDKs.
        // Current and legacy property names keep logs consistent across Java releases.
        info.ArgumentList.Add("-Dstdout.encoding=UTF-8");
        info.ArgumentList.Add("-Dstderr.encoding=UTF-8");
        info.ArgumentList.Add("-Dsun.stdout.encoding=UTF-8");
        info.ArgumentList.Add("-Dsun.stderr.encoding=UTF-8");
        if (metadata.TryGetProperty("logging", out var logging) && logging.TryGetProperty("client", out var loggingClient)
            && loggingClient.TryGetProperty("argument", out var loggingArgument) && loggingClient.TryGetProperty("file", out var loggingFile))
        {
            var configPath = MinecraftMetadata.SafePath(Path.Combine(assetsDirectory, "log_configs"), loggingFile.GetProperty("id").GetString()!);
            if (!File.Exists(configPath)) throw new FileNotFoundException("缺少游戏日志配置，请先补全游戏文件。", configPath);
            replacements["path"] = configPath;
            info.ArgumentList.Add(MinecraftMetadata.Expand(loggingArgument.GetString()!, replacements));
        }
        info.ArgumentList.Add(mainClass ?? throw new InvalidDataException("版本缺少启动入口。"));
        foreach (var argument in gameArguments) info.ArgumentList.Add(MinecraftMetadata.Expand(argument, replacements));
        ct.ThrowIfCancellationRequested();
        return info;
    }

    private async Task ObserveAsync(Process process, string? nativeDirectory)
    {
        try
        {
            var output = ReadOutputAsync(process.StandardOutput, "GAME");
            var errors = ReadOutputAsync(process.StandardError, "GAME");
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            if (process.ExitCode == 0) log.Info("游戏已正常退出。");
            else log.Warn($"游戏已退出，退出码 {process.ExitCode}。请查看游戏日志了解原因。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            log.Warn("游戏日志监控已结束：" + ex.Message);
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_process, process)) _process = null; }
            process.Dispose();
            CleanNatives(nativeDirectory);
            NotifyRunningChanged(false);
        }
    }

    private async Task ReadOutputAsync(StreamReader reader, string level)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) log.Write(level, line);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_process is null) return;
            try { if (!_process.HasExited) { log.Info("正在停止本启动器启动的游戏进程。"); _process.Kill(entireProcessTree: true); } }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { log.Warn("无法停止游戏：" + ex.Message); }
        }
    }

    private static void CleanNatives(string? directory)
    {
        if (directory is null) return;
        var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCL", "natives")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void NotifyRunningChanged(bool running)
    {
        if (RunningChanged is null) return;
        foreach (Action<bool> handler in RunningChanged.GetInvocationList())
        {
            try { handler(running); } catch { /* A UI subscriber must not interrupt process cleanup. */ }
        }
    }

    public void Dispose()
    {
        // Closing the launcher never terminates a running game.
        lock (_gate) _disposed = true;
        RunningChanged = null;
    }
}
