using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

internal static class LocalContentArchive
{
    private const string SaveMarker = "mcl-saves-v1.json";
    private const int MaximumSaveManifestBytes = 32 * 1024 * 1024;
    private static readonly string[] PackFolders = ["mods", "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks"];

    internal static string RuntimeDirectory(string root, GameVersion version) => version.ModpackName is null ? root
        : ModpackPaths.SafeTarget(root, $"versions/{version.Id}/game");

    internal static IEnumerable<(string Relative, string Path)> Files(string root, string relative, CancellationToken ct)
    {
        var folder = ModpackPaths.SafeTarget(root, relative);
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            ct.ThrowIfCancellationRequested();
            var item = ModpackPaths.Relative(Path.GetRelativePath(root, path).Replace('\\', '/'));
            ModpackPaths.SafeTarget(root, item);
            if (Directory.Exists(path))
            {
                foreach (var child in Files(root, item, ct)) yield return child;
            }
            else yield return (item, path);
        }
    }

    internal static Task ExportPackAsync(string root, GameVersion version, string output, CancellationToken ct) =>
        Task.Run(async () =>
        {
            if (version.ModpackName is null || string.IsNullOrWhiteSpace(version.ModpackVersion) || !ModpackPaths.IsGameVersion(version.ModpackMinecraft))
                throw new InvalidDataException("请选择由 MCL 导入的整合包。");
            var runtime = RuntimeDirectory(root, version);
            if (!Directory.Exists(runtime)) throw new DirectoryNotFoundException("找不到整合包游戏目录。");
            string[] loaders = version.ModpackLoader switch
            {
                { } loader when loader.StartsWith("Forge ", StringComparison.Ordinal) => ["forge-" + loader[6..]],
                { } fabric => ["fabric-" + fabric],
                _ => []
            };
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                minecraft = new { version = version.ModpackMinecraft, modLoaders = loaders.Select(id => new { id, primary = true }) },
                manifestType = "minecraftModpack",
                manifestVersion = 1,
                name = version.ModpackName,
                version = version.ModpackVersion,
                files = Array.Empty<object>(),
                overrides = "overrides",
                mclShare = 1
            });
            await WriteAsync(output, runtime, PackFolders, "overrides/", manifest, ct);
        }, ct);

    internal static Task BackupSavesAsync(string root, GameVersion version, string output, CancellationToken ct) =>
        Task.Run(() => WriteAsync(output, RuntimeDirectory(root, version), ["saves"], "", null, ct), ct);

    private static async Task WriteAsync(string output, string root, string[] folders, string prefix,
        byte[]? packManifest, CancellationToken ct)
    {
        output = Path.GetFullPath(output);
        foreach (var folder in folders)
        {
            var path = ModpackPaths.SafeTarget(root, folder);
            if (output.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择要打包的文件夹以外的位置保存 ZIP。");
        }
        var scratch = output + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            long total = 0;
            var count = 0;
            var saveHashes = packManifest is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;
            using (var file = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var folder in folders)
                    foreach (var item in Files(root, folder, ct))
                    {
                        await using var source = File.OpenRead(item.Path);
                        total = checked(total + source.Length);
                        if (++count >= 50000 || total > ModpackArchive.MaximumBytes || source.Length > ModpackArchive.MaximumFileBytes)
                            throw new InvalidDataException("待打包的文件超过 ZIP 容量限制。");
                        await using var entry = archive.CreateEntry(prefix + item.Relative, CompressionLevel.Fastest).Open();
                        using var hash = saveHashes is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await ModpackImportService.CopyAsync(source, entry, source.Length, source.Length, hash, null, ct);
                        if (hash is not null) saveHashes!.Add(item.Relative, Convert.ToHexString(hash.GetHashAndReset()));
                    }
                    if (saveHashes is { Count: 0 }) throw new InvalidDataException("这个游戏目录没有可备份的存档。");
                    var metadata = packManifest ?? JsonSerializer.SerializeToUtf8Bytes(saveHashes);
                    if (metadata.Length > MaximumSaveManifestBytes) throw new InvalidDataException("备份校验清单太大。");
                    await using var header = archive.CreateEntry(packManifest is null ? SaveMarker : "manifest.json").Open();
                    await header.WriteAsync(metadata, ct);
                }
                if (file.Length > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("ZIP 文件超过 8 GB。");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(scratch, output, overwrite: true);
        }
        finally { File.Delete(scratch); }
    }

    internal static Task<string?> RestoreSavesAsync(string root, GameVersion version, string input, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var runtime = RuntimeDirectory(root, version);
            var saves = ModpackPaths.SafeTarget(runtime, "saves");
            var suffix = Guid.NewGuid().ToString("N");
            var staging = ModpackPaths.SafeTarget(runtime, ".mcl-restore-" + suffix);
            var previous = ModpackPaths.SafeTarget(runtime, "saves-before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + suffix[..8]);
            using var file = ModpackImportService.OpenArchive(input);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            if (archive.Entries.Count is < 2 or > 50000 || archive.Entries.Count(e => e.FullName == SaveMarker) != 1
                || archive.GetEntry(SaveMarker) is not { Length: > 0 and <= MaximumSaveManifestBytes } manifest)
                throw new InvalidDataException("请选择 MCL 创建的存档备份 ZIP。");

            using var manifestStream = manifest.Open();
            using var manifestBuffer = new MemoryStream();
            await ModpackImportService.CopyAsync(manifestStream, manifestBuffer, MaximumSaveManifestBytes, manifest.Length, null, null, ct);
            manifestBuffer.Position = 0;
            Dictionary<string, string>? hashes;
            try { hashes = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(manifestBuffer, cancellationToken: ct); }
            catch (JsonException ex) { throw new InvalidDataException("存档备份的校验清单损坏。", ex); }
            if (hashes is null || hashes.Count != archive.Entries.Count - 1)
                throw new InvalidDataException("存档备份的校验清单不完整。");

            long total = 0;
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.FullName == SaveMarker) continue;
                    if (!entry.FullName.StartsWith("saves/", StringComparison.Ordinal) || entry.Length > ModpackArchive.MaximumFileBytes)
                        throw new InvalidDataException("存档备份包含无效文件。");
                    ModpackArchive.EnsureRegularFile(entry);
                    if (!hashes.Remove(entry.FullName, out var expected) || !ModpackPaths.IsHex(expected, 64)
                        || (total += entry.Length) > ModpackArchive.MaximumBytes)
                        throw new InvalidDataException("存档备份包含重复文件或超过容量限制。");
                    var path = ModpackPaths.SafeTarget(staging, entry.FullName["saves/".Length..]);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var source = entry.Open();
                    await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await ModpackImportService.CopyAsync(source, target, entry.Length, entry.Length, hash, null, ct);
                    if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("存档文件校验失败，原存档未替换。");
                }
                ct.ThrowIfCancellationRequested();
                var hadSaves = Directory.Exists(saves);
                if (hadSaves) Directory.Move(saves, previous);
                try { Directory.Move(staging, saves); }
                catch
                {
                    if (hadSaves && !Directory.Exists(saves)) Directory.Move(previous, saves);
                    throw;
                }
                return hadSaves ? previous : null;
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }, ct);

}
