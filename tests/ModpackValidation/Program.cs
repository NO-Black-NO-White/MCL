using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCL.Models;
using MCL.Services;

if (args is ["--live", var testGameDirectory, var javaPath])
{
    await TestLiveAsync(testGameDirectory, javaPath);
    return;
}

if (args is ["--preview", var archive])
{
    Console.WriteLine(JsonSerializer.Serialize(await ModpackImportService.PreviewAsync(archive)));
    return;
}

if (args is ["--forge-live", var forgeGame, var forgeJava])
{
    await TestForgeLiveAsync(forgeGame, forgeJava);
    return;
}

if (args is ["--forge-launch", var existingForgeGame, var installedJava])
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var installedVersion = (await new VersionCatalog().ScanAsync(existingForgeGame)).Single(item => item.ModpackLoader == "Forge 47.3.22");
    await LaunchForgeAsync(existingForgeGame, installedJava, installedVersion, deadline.Token);
    return;
}

var root = Directory.CreateTempSubdirectory("MCL-pack-qa-").FullName;
using var fixture = new FixtureServer();
using var http = new HttpClient(fixture);
using var minecraft = new MinecraftDownloadService(http);
using var importer = new ModpackImportService(minecraft, http);
var path = Path.Combine(root, "fixture.mrpack");
var game = Path.Combine(root, "isolated");
var cursePath = Path.Combine(root, "forge.zip");
WriteCurseForge(cursePath, "overrides/config/sample.txt", files: [new { projectID = 12, fileID = 34, required = true }]);
var curse = await ModpackImportService.PreviewAsync(cursePath);
Check(curse is { CurseForge: true, Minecraft: "1.20.1", Forge: "47.3.22", Fabric: null, RequiredFiles: 1 }
    && curse.Version.StartsWith("ZIP-", StringComparison.Ordinal), "CurseForge ZIP preview detects Forge and handles a blank version field");
WriteCurseForge(cursePath, "overrides/../escaped.txt");
await Reject<InvalidDataException>(() => ModpackImportService.PreviewAsync(cursePath), "CurseForge ZIP overrides cannot escape the instance");
WriteCurseForge(cursePath, "overrides/config/sample.txt", [
    new { projectID = 12, fileID = 34, required = true },
    new { projectID = 13, fileID = 35, required = true },
    new { projectID = 14, fileID = 36, required = true }
], "fabric-0.16.14");
var cursePreview = await ModpackImportService.PreviewAsync(cursePath);
var curseGame = Path.Combine(root, "curse-instance");
var installed = await importer.InstallAsync(cursePreview, curseGame, false);
var installedGame = Path.Combine(curseGame, "versions", installed.InstanceId, "game");
Check(File.Exists(Path.Combine(installedGame, "mods", "fixture.jar"))
    && File.Exists(Path.Combine(installedGame, "resourcepacks", "texture.zip"))
    && File.Exists(Path.Combine(installedGame, "shaderpacks", "light.zip"))
    && File.Exists(Path.Combine(installedGame, "config", "sample.txt")), "CurseForge installs verified files and bundled settings without credentials");
var sourceVersion = (await new VersionCatalog().ScanAsync(curseGame)).Single(version => version.Id == installed.InstanceId);
var savePath = Path.Combine(Directory.CreateDirectory(Path.Combine(installedGame, "saves", "TestWorld")).FullName, "level.dat");
await File.WriteAllTextAsync(savePath, "backup state");
await File.WriteAllTextAsync(Path.Combine(installedGame, "options.txt"), "local preferences");
var sharedArchive = Path.Combine(root, "shared.zip");
await LocalContentArchive.ExportPackAsync(curseGame, sourceVersion, sharedArchive, default);
var sharedPreview = await ModpackImportService.PreviewAsync(sharedArchive);
Check(sharedPreview is { MclShare: true, Minecraft: "1.20.1", Fabric: "0.16.14" }, "Share export preserves the matching Minecraft version and loader");
var friendGame = Path.Combine(root, "friend");
var sharedInstall = await importer.InstallAsync(sharedPreview, friendGame, false);
var friendVersion = (await new VersionCatalog().ScanAsync(friendGame)).Single(version => version.Id == sharedInstall.InstanceId);
var friendRuntime = Path.Combine(friendVersion.VersionDirectory, "game");
Check(!Directory.Exists(Path.Combine(friendRuntime, "saves")) && !File.Exists(Path.Combine(friendRuntime, "options.txt"))
    && File.Exists(Path.Combine(friendRuntime, "config", "sample.txt")), "Shared pack preserves configuration and excludes saves and personal preferences");
Check(await ModpackComparison.FingerprintAsync(curseGame, sourceVersion, default)
    == await ModpackComparison.FingerprintAsync(friendGame, friendVersion, default), "Shared pack installs with the same mod fingerprint");
await File.WriteAllTextAsync(Path.Combine(friendRuntime, "mods", "fixture.jar"), "modified mod");
await File.WriteAllTextAsync(Path.Combine(friendRuntime, "mods", "extra.jar"), "extra mod");
var comparison = await ModpackComparison.CompareAsync(friendGame, friendVersion, sharedArchive, default);
Check(comparison.Contains("文件内容不同") && comparison.Contains("fixture.jar") && comparison.Contains("extra.jar"), "Share comparison lists changed and extra mods");
File.Delete(Path.Combine(friendRuntime, "mods", "fixture.jar"));
Check((await ModpackComparison.CompareAsync(friendGame, friendVersion, sharedArchive, default)).Contains("缺少"), "Share comparison reports missing mods");
var savesBackup = Path.Combine(root, "saves.zip");
await LocalContentArchive.BackupSavesAsync(curseGame, sourceVersion, savesBackup, default);
await File.WriteAllTextAsync(savePath, "new state");
var previousSaves = await LocalContentArchive.RestoreSavesAsync(curseGame, sourceVersion, savesBackup, default);
Check(previousSaves is not null && File.ReadAllText(savePath) == "backup state"
    && File.ReadAllText(Path.Combine(previousSaves, "TestWorld", "level.dat")) == "new state", "Save restoration keeps the pre-restore world");
await Reject<InvalidDataException>(() => LocalContentArchive.RestoreSavesAsync(curseGame, sourceVersion, sharedArchive, default),
    "A share archive cannot be mistaken for a save backup");
Check(File.ReadAllText(savePath) == "backup state", "Rejected restoration leaves the current world intact");
using (var tampered = ZipFile.Open(savesBackup, ZipArchiveMode.Update))
{
    var entry = tampered.GetEntry("saves/TestWorld/level.dat")!;
    using var stream = entry.Open();
    await stream.WriteAsync(Encoding.UTF8.GetBytes("broken state"));
}
await Reject<InvalidDataException>(() => LocalContentArchive.RestoreSavesAsync(curseGame, sourceVersion, savesBackup, default),
    "A modified backup file is rejected even when its size is unchanged");
Check(File.ReadAllText(savePath) == "backup state", "Checksum failure leaves the current world intact");
SavePreviewValidation.Run(curseGame, sourceVersion);
Check(fixture.Requests.ContainsKey("https://api.curse.tools/v1/mods/files")
    && fixture.Requests.ContainsKey("https://edge.forgecdn.net/files/0/34/fixture.jar")
    && !fixture.Requests.ContainsKey("https://mod.mcimirror.top/files/0/34/fixture.jar"),
    "Curse.Tools metadata and a working direct file take priority");
Check(fixture.Requests.ContainsKey("https://edge.forgecdn.net/files/0/35/texture.zip")
    && fixture.Requests.ContainsKey("https://mod.mcimirror.top/files/0/35/texture.zip")
    && fixture.Requests.ContainsKey("https://mediafilez.forgecdn.net/files/0/35/texture.zip"),
    "A failed direct download falls back to MCIM and follows an approved CDN redirect");
Check(fixture.Requests.ContainsKey("https://edge.forgecdn.net/files/0/36/light.zip")
    && fixture.Requests.ContainsKey("https://mod.mcimirror.top/files/0/36/light.zip")
    && fixture.Requests.ContainsKey("https://cdn.modrinth.com/data/fixture/versions/1/light.zip"),
    "A hash-matched Modrinth download fills in when both CurseForge file routes fail");
fixture.HideCurseForgeDownload = true;
await Reject<IOException>(() => importer.InstallAsync(cursePreview, curseGame, false), "A required CurseForge file without a download URL prevents incomplete installation");
var supplements = Directory.CreateDirectory(Path.Combine(root, "supplements")).FullName;
var supplement = Path.Combine(supplements, "fixture.jar");
await File.WriteAllBytesAsync(supplement, new byte[FixtureServer.ModBytes.Length]);
await Reject<IOException>(() => importer.InstallAsync(cursePreview, curseGame, false, localFilesDirectory: supplements),
    "A local file with the wrong checksum cannot fill a missing CurseForge dependency");
Check(Directory.GetDirectories(Path.Combine(curseGame, "versions"), "pack-*").Length == 1
    && !Directory.EnumerateFileSystemEntries(Path.Combine(curseGame, ".mcl-imports")).Any(),
    "Rejected local supplements leave existing instances intact");
await File.WriteAllBytesAsync(supplement, FixtureServer.ModBytes);
var supplemented = await importer.InstallAsync(cursePreview, curseGame, false, localFilesDirectory: supplements);
Check((await File.ReadAllBytesAsync(Path.Combine(curseGame, "versions", supplemented.InstanceId, "game", "mods", "fixture.jar")))
    .SequenceEqual(FixtureServer.ModBytes), "A matching local file fills a missing CurseForge dependency");
fixture.HideCurseForgeDownload = false;
fixture.PrimaryMetadataUnavailable = true;
var mirrorOnly = await importer.InstallAsync(cursePreview, curseGame, false);
Check(File.Exists(Path.Combine(curseGame, "versions", mirrorOnly.InstanceId, "game", "mods", "fixture.jar"))
    && fixture.Requests.ContainsKey("https://mod.mcimirror.top/files/0/34/fixture.jar"),
    "An outage in Curse.Tools metadata falls back to MCIM");
fixture.PrimaryMetadataUnavailable = false;
var originalSave = Path.Combine(game, "saves", "user-placeholder");
Directory.CreateDirectory(originalSave);
var original = Path.Combine(originalSave, "sentinel");
await File.WriteAllTextAsync(original, "leave this alone");

WritePack(path, "fabric-loader", [
    Mod("mods/required.jar", FixtureServer.ModUrl, FixtureServer.ModBytes),
    Mod("mods/optional.jar", FixtureServer.OptionalUrl, FixtureServer.ModBytes, "optional"),
    Mod("mods/server.jar", FixtureServer.ServerUrl, FixtureServer.ModBytes, "unsupported")
], new Dictionary<string, string>
{
    ["overrides/config/sample.txt"] = "default",
    ["client-overrides/config/sample.txt"] = "client",
    ["server-overrides/config/server.txt"] = "server",
    ["overrides/saves/test/level.dat"] = "fixture"
});
var preview = await ModpackImportService.PreviewAsync(path);
Check(preview is { Name: "QA Pack", Minecraft: "1.20.1", Fabric: "0.16.14", RequiredFiles: 1, OptionalFiles: 1 }, "Preview resolves loader and client file counts");
var result = await importer.InstallAsync(preview, game, includeOptional: false);
var versions = await new VersionCatalog().ScanAsync(game);
var version = versions.Single(v => v.Id == result.InstanceId);
var instance = Path.Combine(version.VersionDirectory, "game");
Check(version.IsValid && version.ModpackName == "QA Pack" && version.InheritsFrom == "1.20.1", "Completed instance appears in the catalog with its base dependency");
Check(File.Exists(Path.Combine(instance, "mods", "required.jar"))
    && !fixture.Requests.ContainsKey(FixtureServer.OptionalUrl) && !fixture.Requests.ContainsKey(FixtureServer.ServerUrl),
    "Required files installed and optional / server-only files excluded");
Check(await File.ReadAllTextAsync(Path.Combine(instance, "config", "sample.txt")) == "client"
    && !File.Exists(Path.Combine(instance, "config", "server.txt"))
    && await File.ReadAllTextAsync(original) == "leave this alone", "Client overrides take priority and shared user files stay intact");
Check(!Directory.EnumerateFileSystemEntries(Path.Combine(game, ".mcl-imports")).Any(), "Successfully staged installation was moved into the catalog");
var deletion = new VersionManagementService();
var packNotice = await deletion.InspectAsync(game, version);
Check(packNotice.LocalDataKinds.Contains("独立存档") && packNotice.LocalDataKinds.Contains("模组"), "Deletion preview includes instance saves and mods");
await Reject<InvalidOperationException>(() => deletion.InspectAsync(game, versions.Single(v => v.Id == "1.20.1")), "Base version cannot be deleted while the pack depends on it");
var second = await importer.InstallAsync(preview, game, includeOptional: true);
Check(second.InstanceId != result.InstanceId && fixture.Requests[FixtureServer.OptionalUrl] == 1, "Re-import gives a separate instance and honors optional file selection");

var fakeFile = Mod("mods/bad.jar", FixtureServer.ModUrl, new byte[FixtureServer.ModBytes.Length]);
WritePack(path, null, [fakeFile]);
await Reject<IOException>(async () => await importer.InstallAsync(await ModpackImportService.PreviewAsync(path), game, false),
    "Mismatched content cannot be installed");
Check((await new VersionCatalog().ScanAsync(game)).Count == 3 && !Directory.EnumerateFileSystemEntries(Path.Combine(game, ".mcl-imports")).Any(),
    "A failed download leaves neither a version nor a staging directory");

WritePack(path, null, [Mod("mods/wait.jar", FixtureServer.BlockUrl, FixtureServer.ModBytes)]);
using (var cancellation = new CancellationTokenSource())
{
    var installing = importer.InstallAsync(await ModpackImportService.PreviewAsync(path), game, false, ct: cancellation.Token);
    await fixture.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cancellation.Cancel();
    await Reject<OperationCanceledException>(() => installing, "Cancel interrupts downloads");
    Check((await new VersionCatalog().ScanAsync(game)).Count == 3 && !Directory.EnumerateFileSystemEntries(Path.Combine(game, ".mcl-imports")).Any(),
        "Cancellation cleans temporary files and retains completed versions");
}
var old = await ModpackImportService.PreviewAsync(path);
WritePack(path, "forge", []);
await Reject<IOException>(() => importer.InstallAsync(old, game, false), "Changing the archive after preview requires a fresh selection");
await Reject<NotSupportedException>(() => ModpackImportService.PreviewAsync(path), "Forge is identified and rejected clearly");
WritePack(path, null, [Mod("mods/../outside.jar", FixtureServer.ModUrl, FixtureServer.ModBytes)]);
await Reject<InvalidDataException>(() => ModpackImportService.PreviewAsync(path), "Manifest cannot write outside of its instance");
WritePack(path, null, [], new Dictionary<string, string> { ["overrides/../escaped.txt"] = "bad" });
await Reject<InvalidDataException>(() => ModpackImportService.PreviewAsync(path), "Archive overrides cannot write outside of their instance");
WritePack(path, null, [Mod("mods/test.jar", "https://example.invalid/payload", FixtureServer.ModBytes)]);
await Reject<NotSupportedException>(() => ModpackImportService.PreviewAsync(path), "Unapproved mod download source is refused");

var otherGame = Path.Combine(root, "existing-install");
var otherVersion = Path.Combine(otherGame, "versions", "1.20.1");
Directory.CreateDirectory(otherVersion);
var otherJson = Path.Combine(otherVersion, "1.20.1.json");
var otherJar = Path.Combine(otherVersion, "1.20.1.jar");
const string otherMetadata = """{"id":"1.20.1","mainClass":"net.minecraft.client.main.Main","extra":"local"}""";
await File.WriteAllTextAsync(otherJson, otherMetadata);
await File.WriteAllTextAsync(otherJar, "local jar");
WritePack(path, "fabric-loader", [Mod("mods/required.jar", FixtureServer.ModUrl, FixtureServer.ModBytes)]);
var cached = await importer.InstallAsync(await ModpackImportService.PreviewAsync(path), otherGame, false);
var cachedVersions = await new VersionCatalog().ScanAsync(otherGame);
Check(cachedVersions.Count == 2 && cachedVersions.Any(candidate => candidate.Id == cached.InstanceId
    && candidate is { IsValid: true, InheritsFrom: null, ModpackMinecraft: "1.20.1" }), "Conflicting vanilla version gets a separately cached official base for the pack");
Check(await File.ReadAllTextAsync(otherJson) == otherMetadata && await File.ReadAllTextAsync(otherJar) == "local jar",
    "Existing launcher's version JSON and JAR are preserved");
var otherNotice = await deletion.InspectAsync(otherGame, cachedVersions.Single(candidate => candidate.Id == "1.20.1"));
Check(otherNotice.DirectoryPath == otherVersion, "An instance with a cached base does not depend on the same-named local version");
var cachedPack = cachedVersions.Single(candidate => candidate.Id == cached.InstanceId);
var cachedJar = Directory.EnumerateFiles(Path.Combine(otherGame, ".mcl-base-versions"), "*.jar", SearchOption.AllDirectories).Single();
File.Delete(cachedJar);
var cachedRepair = await minecraft.RepairAsync(cachedPack, otherGame, null, default);
Check(cachedRepair.DownloadedFiles == 1 && File.Exists(cachedJar) && await File.ReadAllTextAsync(otherJar) == "local jar"
    && await File.ReadAllTextAsync(otherJson) == otherMetadata, "Pack repair targets its cached official base and preserves a customized same-name version");
await Reject<IOException>(() => minecraft.RepairAsync(cachedVersions.Single(candidate => candidate.Id == "1.20.1"), otherGame, null, default),
    "Repair refuses to overwrite local customized version metadata");
Check(await File.ReadAllTextAsync(otherJson) == otherMetadata, "Rejected repair keeps customized metadata");

var repairGame = Path.Combine(root, "repair-game");
var repairVersion = (await minecraft.GetVersionsAsync()).Single();
await minecraft.InstallAsync(repairVersion, repairGame);
var installedVanilla = (await new VersionCatalog().ScanAsync(repairGame)).Single();
var repairJar = Path.ChangeExtension(installedVanilla.JsonPath, ".jar");
var repairIndex = Path.Combine(repairGame, "assets", "indexes", "fixture.json");
var repairLog = Path.Combine(repairGame, "assets", "log_configs", "client.xml");
var localFiles = new[] { "saves/world/level.dat", "mods/my-mod.jar", "config/my-config.json" };
foreach (var relative in localFiles)
{
    var local = Path.Combine(repairGame, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
    await File.WriteAllTextAsync(local, "keep");
}
var before = File.GetLastWriteTimeUtc(installedVanilla.JsonPath);
await File.WriteAllBytesAsync(repairJar, new byte[new FileInfo(repairJar).Length]);
await File.WriteAllTextAsync(repairIndex, "bad index");
File.Delete(repairLog);
var repair = await minecraft.RepairAsync(installedVanilla, repairGame, null, default);
Check(repair.DownloadedFiles == 3 && File.Exists(repairLog) && await File.ReadAllTextAsync(repairJar) == "client fixture"
    && localFiles.All(relative => File.ReadAllText(Path.Combine(repairGame, relative)) == "keep")
    && File.GetLastWriteTimeUtc(installedVanilla.JsonPath) == before, "Repair replaces damaged or missing official files and preserves local content and valid metadata");
var knownRequests = fixture.Requests.Where(entry => entry.Key.Contains("piston-data")).ToDictionary();
Check((await minecraft.RepairAsync(installedVanilla, repairGame, null, default)).DownloadedFiles == 0
    && knownRequests.All(entry => fixture.Requests[entry.Key] == entry.Value), "A complete installation does not download its files again");

var diagnostic = new RoomDiagnostic(hosting: false, RoomRoute.Ipv6);
const string secret = "MCL6/2600::7/45500/secret-invite";
diagnostic.Record("检查 IPv6 直连");
diagnostic.Record(secret);
diagnostic.SetFailure(new IOException(secret, new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.TimedOut)));
var report = diagnostic.Format("0.9.2");
Check(report.Contains("IPv6 直连") && report.Contains("TimedOut") && !report.Contains("2600::7") && !report.Contains("secret-invite"),
    "Diagnostic report retains route and failure category and excludes raw endpoints and invite data");
Check(LaunchTroubleshooter.FromOutput("java.lang.UnsupportedClassVersionError: fixture")?.Fix == LaunchFix.Java
    && LaunchTroubleshooter.FromOutput("Error: Invalid or corrupt jarfile example.jar")?.Fix == LaunchFix.Repair
    && LaunchTroubleshooter.FromOutput("Could not reserve enough space for object heap")?.Fix == LaunchFix.Memory
    && LaunchTroubleshooter.FromOutput("normal game log") is null, "Common JVM failures map to the appropriate actions");
Console.WriteLine("MODPACK_VALIDATION_OK");

static async Task TestForgeLiveAsync(string isolatedGame, string javaPath)
{
    Directory.CreateDirectory(isolatedGame);
    var path = Path.Combine(isolatedGame, "forge-test.zip");
    WriteCurseForge(path, "overrides/config/mcl-test.txt");
    using var downloads = new MinecraftDownloadService();
    using var installer = new ModpackImportService(downloads);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var result = await installer.InstallAsync(await ModpackImportService.PreviewAsync(path), isolatedGame, false,
        new ProgressPrinter(), timeout.Token, javaPath);
    var version = (await new VersionCatalog().ScanAsync(isolatedGame)).Single(item => item.Id == result.InstanceId);
    Check(version is { IsValid: true, ModpackLoader: "Forge 47.3.22" }, "Forge instance is ready in the version list");
    await LaunchForgeAsync(isolatedGame, javaPath, version, timeout.Token);
}

static async Task LaunchForgeAsync(string isolatedGame, string javaPath, GameVersion version, CancellationToken ct)
{
    var java = await new JavaDiscovery().InspectAsync(javaPath, ct) ?? throw new Exception("Missing Java");
    var log = new LogService(isolatedGame);
    using var launcher = new GameLaunchService(log);
    var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    log.EntryAdded += entry =>
    {
        if (entry.Message.Contains("textures/atlas", StringComparison.Ordinal)) startup.TrySetResult();
        if (entry.Message.Contains("Exception", StringComparison.Ordinal) || entry.Message.Contains("ModLauncher", StringComparison.Ordinal))
            Console.WriteLine(entry.Message);
    };
    await launcher.LaunchAsync(new LauncherSettings { GameDirectory = isolatedGame, PlayerName = "MclForgeTest", MemoryMb = 2048 },
        version, java, ct);
    try
    {
        for (var attempt = 0; attempt < 160 && launcher.IsRunning && !startup.Task.IsCompleted; attempt++) await Task.Delay(500, ct);
        Check(startup.Task.IsCompletedSuccessfully, "Minecraft reached texture initialization through Forge");
        Console.WriteLine("FORGE_LIVE_OK");
    }
    finally { if (launcher.IsRunning) launcher.Stop(); }
}

static void WriteCurseForge(string path, string overridePath, object[]? files = null, string loader = "forge-47.3.22")
{
    using var output = File.Create(path);
    using var zip = new ZipArchive(output, ZipArchiveMode.Create);
    using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
        writer.Write(JsonSerializer.Serialize(new
        {
            manifestType = "minecraftModpack", manifestVersion = 1, name = "QA Forge Pack", version = "",
            minecraft = new { version = "1.20.1", modLoaders = new[] { new { id = loader, primary = true } } },
            overrides = "overrides", files = files ?? []
        }));
    using var configuration = new StreamWriter(zip.CreateEntry(overridePath).Open());
    configuration.Write("isolated");
}

static async Task TestLiveAsync(string isolatedGame, string javaPath)
{
    var qa = Directory.CreateTempSubdirectory("MCL-pack-live-").FullName;
    var packPath = Path.Combine(qa, "fabric-api.mrpack");
    var mod = new
    {
        path = "mods/fabric-api.jar", fileSize = 2137232,
        hashes = new
        {
            sha1 = "3e9cdd3e2f827ca9a259df9eb8e31949437b6bd4",
            sha512 = "de9da2344043b3e9ef9fb7dbfff5f2577b06000c4f73f2a37959a80123141169084cece19133bd299b45feaaa4648bdaca2ea085684888cc8c55cc9134a477ca"
        },
        downloads = new[] { "https://cdn.modrinth.com/data/P7dR8mSH/versions/rvI2dfzR/fabric-api-0.92.12%2B1.20.1.jar" }
    };
    WritePack(packPath, "fabric-loader", [mod], new Dictionary<string, string> { ["client-overrides/config/mcl-test.txt"] = "isolated" });
    using var downloads = new MinecraftDownloadService();
    using var service = new ModpackImportService(downloads);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    ProgressPrinter progress = new();
    var installed = await service.InstallAsync(await ModpackImportService.PreviewAsync(packPath), isolatedGame, false, progress, timeout.Token);
    var version = (await new VersionCatalog().ScanAsync(isolatedGame)).Single(v => v.Id == installed.InstanceId);
    var runtime = new JavaInstallation(javaPath, 21, "21", true);
    var log = new LogService(qa);
    using var launcher = new GameLaunchService(log);
    var messages = new ConcurrentQueue<string>();
    log.EntryAdded += entry =>
    {
        if (entry.Message.Contains("Loading Minecraft") || entry.Message.Contains("fabric-api") || entry.Message.Contains("Created:") || entry.Message.Contains("Exception"))
        {
            messages.Enqueue(entry.Message);
            if (messages.Count < 18) Console.WriteLine(entry.Message);
        }
    };
    await launcher.LaunchAsync(new LauncherSettings { GameDirectory = isolatedGame, PlayerName = "MclPackTest", MemoryMb = 2048 },
        version, runtime, timeout.Token);
    try
    {
        var end = DateTime.UtcNow.AddSeconds(65);
        while (launcher.IsRunning && DateTime.UtcNow < end && !messages.Any(message => message.Contains("textures/atlas")))
            await Task.Delay(500);
        Check(messages.Any(message => message.Contains("Loading Minecraft 1.20.1 with Fabric Loader 0.16.14")), "Java loaded Minecraft through the installed Fabric Loader");
        Check(messages.Any(message => message.Contains("fabric-api 0.92.12")), "Fabric discovered the Modrinth mod");
        Check(messages.Any(message => message.Contains("textures/atlas")), "Minecraft initialized its textures");
        Check(File.Exists(Path.Combine(version.VersionDirectory, "game", "logs", "latest.log")), "Game writes its runtime log to its own instance");
        Console.WriteLine("MODPACK_LIVE_OK " + qa);
    }
    finally { if (launcher.IsRunning) launcher.Stop(); }
}

static void WritePack(string path, string? loader, object[] mods, IDictionary<string, string>? overrides = null)
{
    using var output = File.Create(path);
    using var zip = new ZipArchive(output, ZipArchiveMode.Create);
    var dependencies = new Dictionary<string, string> { ["minecraft"] = "1.20.1" };
    if (loader is not null) dependencies[loader] = "0.16.14";
    void Add(string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(text);
    }
    Add("modrinth.index.json", JsonSerializer.Serialize(new { formatVersion = 1, game = "minecraft", versionId = "1", name = "QA Pack", dependencies, files = mods }));
    if (overrides is not null)
        foreach (var (name, text) in overrides) Add(name, text);
}

static object Mod(string path, string url, byte[] bytes, string client = "required") => new
{
    path, fileSize = bytes.Length, downloads = new[] { url },
    hashes = new { sha1 = Convert.ToHexString(SHA1.HashData(bytes)), sha512 = Convert.ToHexString(SHA512.HashData(bytes)) },
    env = new { client, server = "required" }
};

static async Task Reject<T>(Func<Task> work, string message) where T : Exception
{
    try { await work(); }
    catch (T) { Check(true, message); return; }
    Check(false, message);
}

static void Check(bool result, string message)
{
    if (!result) throw new InvalidOperationException("FAIL " + message);
    Console.WriteLine("PASS " + message);
}

sealed class ProgressPrinter : IProgress<InstallProgress>
{
    private string? _stage;
    public void Report(InstallProgress state)
    {
        if (_stage == state.Stage) return;
        _stage = state.Stage;
        Console.WriteLine($"{state.Stage}: {state.Detail}");
    }
}

sealed class FixtureServer : HttpMessageHandler
{
    public const string ModUrl = "https://cdn.modrinth.com/required.jar";
    public const string OptionalUrl = "https://cdn.modrinth.com/optional.jar";
    public const string ServerUrl = "https://cdn.modrinth.com/server.jar";
    public const string BlockUrl = "https://cdn.modrinth.com/block.jar";
    private const string FabricProfile = "https://meta.fabricmc.net/v2/versions/loader/1.20.1/0.16.14/profile/json";
    private const string FabricJar = "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.14/fabric-loader-0.16.14.jar";
    public static readonly byte[] ModBytes = Encoding.UTF8.GetBytes("mod fixture");
    public ConcurrentDictionary<string, int> Requests { get; } = new();
    public bool HideCurseForgeDownload { get; set; }
    public bool PrimaryMetadataUnavailable { get; set; }
    private static readonly byte[] LightBytes = Encoding.ASCII.GetBytes("fixture shader");
    public TaskCompletionSource BlockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, byte[]> _data = new();

    public FixtureServer()
    {
        (string Url, string Sha1, int Size) Add(string name, byte[] bytes)
        {
            var sha1 = Convert.ToHexString(SHA1.HashData(bytes));
            var url = $"https://piston-data.mojang.com/v1/objects/{sha1}/{name}";
            _data[url] = bytes;
            return (url, sha1, bytes.Length);
        }
        var jar = Add("client.jar", Encoding.UTF8.GetBytes("client fixture"));
        var idx = Add("assets.json", JsonSerializer.SerializeToUtf8Bytes(new { objects = new { } }));
        var log = Add("client.xml", Encoding.UTF8.GetBytes("<Configuration/>"));
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "1.20.1", type = "release", mainClass = "net.minecraft.client.main.Main",
            downloads = new { client = new { url = jar.Url, sha1 = jar.Sha1, size = jar.Size } }, libraries = Array.Empty<object>(),
            arguments = new { game = new[] { "--gameDir", "${game_directory}" }, jvm = Array.Empty<string>() },
            assetIndex = new { id = "fixture", url = idx.Url, sha1 = idx.Sha1, size = idx.Size },
            logging = new { client = new { file = new { id = "client.xml", url = log.Url, sha1 = log.Sha1, size = log.Size } } }
        });
        var hash = Convert.ToHexString(SHA1.HashData(json));
        var versionUrl = $"https://piston-meta.mojang.com/v1/packages/{hash}/1.20.1.json";
        _data[versionUrl] = json;
        _data[MinecraftDownloadService.ManifestUri.AbsoluteUri] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            versions = new[] { new { id = "1.20.1", type = "release", sha1 = hash, url = versionUrl, releaseTime = "2023-06-12T00:00:00Z" } }
        });
        _data[FabricProfile] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "fabric-loader-0.16.14-1.20.1", inheritsFrom = "1.20.1", mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            arguments = new { jvm = Array.Empty<string>(), game = Array.Empty<string>() },
            libraries = new[] { new { name = "net.fabricmc:fabric-loader:0.16.14", url = "https://maven.fabricmc.net/" } }
        });
        _data[FabricJar] = ModBytes;
        _data[FabricJar + ".sha1"] = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA1.HashData(ModBytes)));
        _data[ModUrl] = _data[OptionalUrl] = _data[ServerUrl] = ModBytes;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.AddOrUpdate(url, 1, (_, count) => count + 1);
        if (url is "https://api.curse.tools/v1/mods/files" or "https://api.curse.tools/v1/mods"
            or "https://mod.mcimirror.top/curseforge/v1/mods/files" or "https://mod.mcimirror.top/curseforge/v1/mods")
        {
            if (request.Method != HttpMethod.Post || request.Headers.Contains("x-api-key"))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (PrimaryMetadataUnavailable && request.RequestUri.Host == "api.curse.tools")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (url.EndsWith("/files", StringComparison.Ordinal))
            {
                var files = new[] { "fixture.jar", "texture.zip", "light.zip" }.Select((name, index) =>
                {
                    var bytes = index == 2 ? LightBytes : ModBytes;
                    return new
                    {
                        modId = 12 + index, id = 34 + index, fileName = name, fileLength = bytes.Length,
                        downloadUrl = HideCurseForgeDownload && index == 0 ? null : $"https://edge.forgecdn.net/files/0/{34 + index}/{name}",
                        hashes = new[] { new { algo = 1, value = Convert.ToHexString(SHA1.HashData(bytes)) } }
                    };
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = files })) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":13,"links":{"websiteUrl":"https://www.curseforge.com/minecraft/texture-packs/test"}},{"id":14,"links":{"websiteUrl":"https://www.curseforge.com/minecraft/shaders/test"}}]}""") };
        }
        if (url == "https://api.modrinth.com/v2/version_files")
        {
            var hash = Convert.ToHexString(SHA1.HashData(LightBytes)).ToLowerInvariant();
            var hashLookup = await request.Content!.ReadAsStringAsync(ct);
            var result = new Dictionary<string, object>();
            if (hashLookup.Contains(hash, StringComparison.Ordinal))
                result[hash] = new { files = new[] { new { hashes = new { sha1 = hash }, size = LightBytes.Length,
                    url = "https://cdn.modrinth.com/data/fixture/versions/1/light.zip" } } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result)) };
        }
        if (url == "https://cdn.modrinth.com/data/fixture/versions/1/light.zip")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LightBytes) };
        if (request.RequestUri.Host == "edge.forgecdn.net")
            return request.RequestUri.AbsolutePath.EndsWith("/fixture.jar", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ModBytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.RequestUri.Host == "mod.mcimirror.top" && request.RequestUri.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal))
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/light.zip", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new UriBuilder(request.RequestUri) { Host = "mediafilez.forgecdn.net" }.Uri;
            return redirect;
        }
        if (request.RequestUri.Host == "mediafilez.forgecdn.net")
        {
            if (request.Headers.Contains("x-api-key")) throw new InvalidOperationException("An API key was sent to the CDN");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ModBytes) };
        }
        if (url == BlockUrl)
        {
            BlockStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        return _data.TryGetValue(url, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
