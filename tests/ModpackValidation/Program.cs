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

var root = Directory.CreateTempSubdirectory("MCL-pack-qa-").FullName;
using var fixture = new FixtureServer();
using var http = new HttpClient(fixture);
using var minecraft = new MinecraftDownloadService(http);
using var importer = new ModpackImportService(minecraft, http);
var path = Path.Combine(root, "fixture.mrpack");
var game = Path.Combine(root, "isolated");
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
Console.WriteLine("MODPACK_VALIDATION_OK");

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
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "1.20.1", type = "release", mainClass = "net.minecraft.client.main.Main",
            downloads = new { client = new { url = jar.Url, sha1 = jar.Sha1, size = jar.Size } }, libraries = Array.Empty<object>(),
            arguments = new { game = new[] { "--gameDir", "${game_directory}" }, jvm = Array.Empty<string>() },
            assetIndex = new { id = "fixture", url = idx.Url, sha1 = idx.Sha1, size = idx.Size }
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
