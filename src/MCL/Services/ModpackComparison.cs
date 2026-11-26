using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MCL.Models;

namespace MCL.Services;

internal static class ModpackComparison
{
    internal static async Task<string> FingerprintAsync(string root, GameVersion version, CancellationToken ct)
    {
        if (version.ModpackName is null) return Convert.ToHexString(SHA256.HashData([]));
        var files = await ReadInstalledAsync(root, version, ct);
        var text = string.Join('\n', files.Select(item => $"{item.Key.ToUpperInvariant()}:{item.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    internal static async Task<string> CompareAsync(string root, GameVersion version, string path, CancellationToken ct)
    {
        using var archive = ModpackImportService.OpenArchive(path);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        var pack = ModpackArchive.Read(zip, path, new string('0', 64), ct);
        if (!pack.Preview.MclShare)
            throw new InvalidDataException("请选择好友从 MCL 导出的分享包 ZIP。");
        var installed = await ReadInstalledAsync(root, version, ct);
        var incoming = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in pack.Overrides.Where(item => IsMod(item.Path)))
        {
            using var input = item.Entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await ModpackImportService.CopyAsync(input, Stream.Null, item.Entry.Length, item.Entry.Length, hash, null, ct);
            incoming.Add(item.Path, Convert.ToHexString(hash.GetHashAndReset()));
        }

        var changes = new List<string>();
        var game = version.ModpackMinecraft ?? version.Id;
        var loader = version.ModpackLoader is { } l && !l.StartsWith("Forge ", StringComparison.Ordinal) ? "Fabric " + l : version.ModpackLoader ?? version.Kind;
        if (game != pack.Preview.Minecraft) changes.Add($"游戏版本：本机 {game}，分享包 {pack.Preview.Minecraft}");
        if (loader != pack.Preview.LoaderText) changes.Add($"加载器：本机 {loader}，分享包 {pack.Preview.LoaderText}");
        Add("缺少", incoming.Keys.Except(installed.Keys, StringComparer.OrdinalIgnoreCase));
        Add("多出", installed.Keys.Except(incoming.Keys, StringComparer.OrdinalIgnoreCase));
        Add("文件内容不同", incoming.Keys.Where(file => installed.TryGetValue(file, out var sha) && sha != incoming[file]));
        return changes.Count == 0 ? "游戏版本、加载器和模组文件一致。" : string.Join("\n\n", changes);

        void Add(string label, IEnumerable<string> names)
        {
            var files = names.ToArray();
            if (files.Length == 0) return;
            changes.Add($"{label} {files.Length} 个模组\n{string.Join('\n', files)}");
        }
    }

    private static async Task<SortedDictionary<string, string>> ReadInstalledAsync(string root, GameVersion version, CancellationToken ct)
    {
        var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var runtime = LocalContentArchive.RuntimeDirectory(root, version);
        if (!Directory.Exists(runtime)) throw new DirectoryNotFoundException("找不到游戏目录，请刷新版本后重试。");
        foreach (var item in LocalContentArchive.Files(runtime, "mods", ct).Where(item => IsMod(item.Relative)))
        {
            await using var input = File.OpenRead(item.Path);
            files.Add(item.Relative, Convert.ToHexString(await SHA256.HashDataAsync(input, ct)));
        }
        return files;
    }

    private static bool IsMod(string path) => path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase);
}
