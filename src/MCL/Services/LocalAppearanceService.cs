using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using MCL.Models;

namespace MCL.Services;

internal sealed class LocalAppearanceService(string dataDirectory, ModpackImportService downloads)
{
    private const string Loader = "0.20.1";
    private const int MaximumPngBytes = 1024 * 1024;
    private const string OwnershipMarker = ".mcl-local-appearance";
    private static readonly SkinMod Classic = new("14.28",
        "https://cdn.modrinth.com/data/idMHQ4n2/versions/2C8mIbK2/CustomSkinLoader_Fabric-14.28.jar", 189001,
        "243fdcc6ad067c3586805527fa64a6389cc4cbff",
        "5827ae560b37e6c2ab8ee9f6cf1b736da85a6cad90c26ef91dc3b3e9b0298c66d285877df4d92405f98ebe5b1cfcf71738b1aec5eaff2606412c352fe255deec");
    private static readonly SkinMod Universal = new("15.1",
        "https://cdn.modrinth.com/data/idMHQ4n2/versions/yyYvYfZI/CustomSkinLoader_Universal-15.1.jar", 222917,
        "324e6921ecd576e44875981466e7f22170b6541a",
        "1c3ee525200d3dff69b19d4667f0c40155d0a8eedcfed4749ec8883fcf169bcf1123942a87ca73ecf46002fded35ec653bb902b0d3900c42f4eab0323793daf2");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static bool Supports(GameVersion? game) =>
        game is { IsValid: true } && (game.Kind == "原版" || game.ModpackName is not null)
        && Version.TryParse(game.ModpackMinecraft ?? game.Id, out var number)
        && (number >= new Version(1, 14) && number <= new Version(1, 21, 11)
            || number >= new Version(26, 1) && number <= new Version(26, 3));

    internal static string NormalizeModel(string? value) => value == "slim" ? "slim" : "default";

    internal (string Hash, BitmapSource Preview) ImportTexture(string source, bool cape)
        => ImportTexture(ReadBounded(source, MaximumPngBytes), cape);

    internal (string Hash, BitmapSource Preview) ImportTexture(byte[] source, bool cape)
    {
        var image = DecodeTexture(source, cape);
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(output);
        var bytes = output.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        WriteAtomically(TexturePath(hash), bytes);
        return (hash, image);
    }

    internal BitmapSource? PreviewTexture(string? hash, bool cape) =>
        hash is null ? null : DecodeTexture(ReadSavedTexture(hash), cape);

    private string TexturePath(string hash)
    {
        if (!ModpackPaths.IsHex(hash, 64)) throw new InvalidDataException("本地外观文件的编号无效，请重新选择图片。");
        return ModpackPaths.SafeTarget(dataDirectory, $"appearance/textures/{hash.ToLowerInvariant()}.png");
    }

    private byte[] ReadSavedTexture(string hash)
    {
        var bytes = ReadBounded(TexturePath(hash), MaximumPngBytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("保存的外观图片已损坏，请重新选择。");
        return bytes;
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 24 || stream.Length > limit) throw new InvalidDataException("图片大小无效，请选择不超过 1 MB 的 PNG。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static BitmapSource DecodeTexture(byte[] bytes, bool cape)
    {
        if (bytes.Length is < 24 or > MaximumPngBytes)
            throw new InvalidDataException("外观图片大小无效，需要不超过 1 MB 的 PNG。");
        if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("请选择有效的 PNG 图片。");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width != 64 || (cape ? height != 32 : height is not (32 or 64)))
            throw new InvalidDataException(cape ? "披风需要 64×32 像素的 Minecraft PNG。" : "皮肤需要 64×64 或 64×32 像素的 Minecraft PNG。");
        using var input = new MemoryStream(bytes, writable: false);
        var bitmap = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        bitmap.Freeze();
        return bitmap;
    }

    internal async Task<LocalAppearanceLaunch> PrepareAsync(LauncherSettings settings, GameVersion version, CancellationToken ct)
    {
        if (!Supports(version))
            throw new NotSupportedException("这个版本暂不支持本地皮肤和披风。请使用兼容版本，或在「本地角色」取消勾选「启动时应用外观」。");
        if (!Regex.IsMatch(settings.PlayerName, @"\A[A-Za-z0-9_]{1,16}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("请先填写有效的本地玩家名。");
        var minecraft = version.ModpackMinecraft ?? version.Id;
        var mod = Version.Parse(minecraft).Major >= 26 ? Universal : Classic;
        var game = Path.GetFullPath(settings.GameDirectory);
        if (version.ModpackName is not null) game = ModpackPaths.SafeTarget(game, $"versions/{version.Id}/game");
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException("找不到游戏目录，请刷新游戏版本。");

        var skin = settings.LocalSkin is { } skinHash ? ReadSavedTexture(skinHash) : null;
        var cape = settings.LocalCape is { } capeHash ? ReadSavedTexture(capeHash) : null;
        var destination = ModpackPaths.SafeTarget(game, "CustomSkinLoader");
        var marker = ModpackPaths.SafeTarget(destination, OwnershipMarker);
        if (!File.Exists(marker) && Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("此游戏已有独立的 CustomSkinLoader 配置。请关闭 MCL 的「启动时应用外观」以使用原有配置。");
        if (version.ModpackLoader is not null)
            CheckExistingMods(ModpackPaths.SafeTarget(game, "mods"), ct);

        var cache = ModpackPaths.SafeTarget(dataDirectory, "appearance/runtime");
        Directory.CreateDirectory(cache);
        using var preparation = new FileStream(ModpackPaths.SafeTarget(cache, "prepare.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var jar = ModpackPaths.SafeTarget(cache, $"CustomSkinLoader-{mod.Version}.jar");
        if (!await VerifiedDownloader.IsValidAsync(jar, mod.Sha1, mod.Size, ct).ConfigureAwait(false))
            await downloads.DownloadAsync([new Uri(mod.Url)], jar, mod.Size, mod.Size, mod.Sha1, mod.Sha512,
                uri => ModpackArchive.AllowedPackUri(uri.AbsoluteUri) is not null, ct, overwrite: true).ConfigureAwait(false);

        string? emptyMods = null;
        string? fabricFolder = null;
        FabricProfile? fabric = null;
        if (version.ModpackLoader is null)
        {
            fabricFolder = ModpackPaths.SafeTarget(cache, $"fabric/{minecraft}-{Loader}");
            var recipe = ModpackPaths.SafeTarget(fabricFolder, "profile.json");
            fabric = await ReadFabricAsync(fabricFolder, recipe, ct).ConfigureAwait(false);
            if (fabric is null)
            {
                fabric = await downloads.InstallFabricAsync(minecraft, Loader, fabricFolder, null, ct).ConfigureAwait(false);
                WriteAtomically(recipe, JsonSerializer.SerializeToUtf8Bytes(fabric));
            }
            emptyMods = ModpackPaths.SafeTarget(cache, "vanilla-mods");
            Directory.CreateDirectory(emptyMods);
            if (Directory.EnumerateFileSystemEntries(emptyMods).Any())
                throw new IOException("外观组件的专用模组目录包含额外文件，请清理后重试。");
        }

        ct.ThrowIfCancellationRequested();
        WriteGameTextures(destination, marker, settings, mod.Version, skin, cape);
        return new(jar, emptyMods, fabricFolder, fabric);
    }

    private static void CheckExistingMods(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*.jar"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var descriptor = zip.GetEntry("fabric.mod.json");
                if (descriptor is null || descriptor.Length > 256 * 1024) continue;
                using var stream = descriptor.Open();
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String && id.GetString() == "customskinloader")
                    throw new InvalidOperationException("此整合包已自带 CustomSkinLoader。请关闭 MCL 的「启动时应用外观」以使用整合包的配置。");
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
        }
    }

    private static async Task<FabricProfile?> ReadFabricAsync(string folder, string recipe, CancellationToken ct)
    {
        if (!File.Exists(recipe) || new FileInfo(recipe).Length > 64 * 1024) return null;
        try
        {
            var fabric = JsonSerializer.Deserialize<FabricProfile>(await File.ReadAllBytesAsync(recipe, ct).ConfigureAwait(false));
            if (fabric is null || fabric.MainClass is not (ModpackPaths.FabricMain or ModpackPaths.LegacyFabricMain)
                || fabric.Jvm is null || fabric.Jvm.Length > 3 || fabric.Jvm.Any(arg => !ModpackPaths.IsAllowedJvmArgument(arg))
                || fabric.Libraries is null || fabric.Libraries.Length is < 1 or > 64) return null;
            foreach (var lib in fabric.Libraries)
            {
                if (lib is null || lib.Coordinate is null || !ModpackPaths.IsHex(lib.Sha1, 40)) return null;
                var jar = ModpackPaths.SafeTarget(folder, ".mcl-libraries/" + ModpackPaths.LibraryPath(lib.Coordinate));
                if (!File.Exists(jar) || new FileInfo(jar).Length is < 1 or > 64 * 1024 * 1024
                    || !await VerifiedDownloader.IsValidAsync(jar, lib.Sha1, new FileInfo(jar).Length, ct).ConfigureAwait(false)) return null;
            }
            return fabric.Libraries.Any(lib => lib.Coordinate == $"net.fabricmc:fabric-loader:{Loader}") ? fabric : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return null; }
    }

    private static void WriteGameTextures(string folder, string marker, LauncherSettings settings, string modVersion, byte[]? skin, byte[]? cape)
    {
        if (!File.Exists(marker))
        {
            Directory.CreateDirectory(folder);
            if (Directory.EnumerateFileSystemEntries(folder).Any())
                throw new IOException("游戏外观目录已经有其他配置，请关闭 MCL 外观后继续使用。");
            using var ownership = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        var extra = ModpackPaths.SafeTarget(folder, "ExtraList");
        if (Directory.Exists(extra) && Directory.EnumerateFiles(extra).Any(file =>
            Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("游戏外观目录有另外配置的皮肤来源，请关闭 MCL 外观后继续使用。");
        var model = NormalizeModel(settings.LocalSkinModel);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{settings.PlayerName}/{settings.LocalSkin}/{settings.LocalCape}/{model}"))).ToLowerInvariant()[..24];
        var relative = $"LocalSkin/mcl/{fingerprint}";
        if (skin is not null) WriteAtomically(ModpackPaths.SafeTarget(folder, $"{relative}/skins/mcl-{settings.PlayerName}.png"), skin);
        if (cape is not null) WriteAtomically(ModpackPaths.SafeTarget(folder, $"{relative}/capes/mcl-{settings.PlayerName}.png"), cape);
        var config = new
        {
            version = modVersion,
            loadlist = new object[]
            {
                new
                {
                    name = "LocalSkin", type = "Legacy", model, checkPNG = false,
                    skin = skin is null ? "" : $"{relative}/skins/mcl-{{USERNAME}}.png",
                    cape = cape is null ? "" : $"{relative}/capes/mcl-{{USERNAME}}.png",
                    elytra = ""
                },
                new { name = "GameProfile", type = "GameProfile" }
            },
            enableCape = true, enableLocalProfileCache = false, forceDisableCache = true
        };
        WriteAtomically(ModpackPaths.SafeTarget(folder, "CustomSkinLoader.json"), JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions));
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(staging, bytes);
            File.Move(staging, path, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private sealed record SkinMod(string Version, string Url, int Size, string Sha1, string Sha512);
}
