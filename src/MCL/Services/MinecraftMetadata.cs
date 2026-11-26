using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCL.Models;

namespace MCL.Services;

internal static class MinecraftMetadata
{
    private static readonly Regex Placeholder = new(@"\$\{([^}]+)\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static bool IsValidLocalPlayerName(string? name)
        => name is not null && Regex.IsMatch(name, @"\A[A-Za-z0-9_]{1,16}\z", RegexOptions.CultureInvariant);

    internal static int RequiredJava(JsonElement metadata) =>
        metadata.ValueKind == JsonValueKind.Object
        && metadata.TryGetProperty("javaVersion", out var java) && java.ValueKind == JsonValueKind.Object
        && java.TryGetProperty("majorVersion", out var major) && major.ValueKind == JsonValueKind.Number
        && major.TryGetInt32(out var version) && version > 0 ? version : 8;

    internal static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static bool RulesAllow(JsonElement element, JavaInstallation java)
    {
        if (!element.TryGetProperty("rules", out var rules)) return true;
        if (rules.ValueKind != JsonValueKind.Array) throw new InvalidDataException("版本的 rules 配置格式不正确。");
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var name)) matches &= name.GetString() == "windows";
                if (os.TryGetProperty("arch", out var arch))
                {
                    var pattern = arch.GetString() ?? "";
                    var architecture = java.Is64Bit ? "amd64" : "x86";
                    matches &= MatchesPattern(architecture, pattern);
                }
                if (os.TryGetProperty("version", out var version)) matches &= MatchesPattern(Environment.OSVersion.Version.ToString(), version.GetString() ?? "");
            }
            if (rule.TryGetProperty("features", out var features))
            {
                foreach (var feature in features.EnumerateObject())
                {
                    // This local profile enables no demo, quick play, or custom-resolution features.
                    if (feature.Value.ValueKind != JsonValueKind.False) matches = false;
                }
            }
            if (matches) allowed = rule.GetProperty("action").GetString() == "allow";
        }
        return allowed;
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        try { return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150)); }
        catch (ArgumentException ex) { throw new InvalidDataException("版本的系统匹配规则不正确。", ex); }
        catch (RegexMatchTimeoutException ex) { throw new InvalidDataException("版本的系统匹配规则过于复杂。", ex); }
    }

    internal static IEnumerable<string> ReadArguments(JsonElement array, JavaInstallation java)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("版本的启动参数格式不正确。");
        foreach (var argument in array.EnumerateArray())
        {
            if (argument.ValueKind == JsonValueKind.String) yield return argument.GetString()!;
            else if (argument.ValueKind == JsonValueKind.Object && RulesAllow(argument, java))
            {
                if (!argument.TryGetProperty("value", out var value)) throw new InvalidDataException("版本的条件参数缺少 value。");
                if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in value.EnumerateArray()) yield return part.GetString() ?? throw new InvalidDataException("版本的启动参数包含空值。");
                }
                else throw new InvalidDataException("版本的条件参数格式不正确。");
            }
            else if (argument.ValueKind != JsonValueKind.Object) throw new InvalidDataException("版本的启动参数格式不正确。");
        }
    }

    internal static string Expand(string argument, IReadOnlyDictionary<string, string> replacements)
    {
        return Placeholder.Replace(argument, match => replacements.TryGetValue(match.Groups[1].Value, out var value)
            ? value : throw new InvalidDataException($"这个版本使用了暂未支持的启动参数：{match.Value}。"));
    }

    internal static IReadOnlyList<string> SplitLegacyArguments(string commandLine)
    {
        var result = new List<string>();
        var buffer = new StringBuilder();
        var quoted = false;
        var hasValue = false;
        foreach (var character in commandLine)
        {
            if (character == '"') { quoted = !quoted; hasValue = true; }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (hasValue) { result.Add(buffer.ToString()); buffer.Clear(); hasValue = false; }
            }
            else { buffer.Append(character); hasValue = true; }
        }
        if (quoted) throw new InvalidDataException("旧版游戏启动参数中的引号不完整。");
        if (hasValue) result.Add(buffer.ToString());
        return result;
    }

    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("版本元数据包含不安全的文件路径。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("版本元数据包含越界的文件路径。");
        return path;
    }

    internal static string MavenPath(string name, string? classifier = null)
    {
        var extensionParts = name.Split('@', 2);
        var parts = extensionParts[0].Split(':');
        if (parts.Length is < 3 or > 4) throw new InvalidDataException("版本包含无法识别的依赖坐标。");
        var selectedClassifier = classifier ?? (parts.Length == 4 ? parts[3] : null);
        var suffix = string.IsNullOrEmpty(selectedClassifier) ? "" : "-" + selectedClassifier;
        var extension = extensionParts.Length == 2 ? extensionParts[1] : "jar";
        return $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{suffix}.{extension}";
    }

    internal static void ExtractNatives(string archivePath, string destination, IReadOnlyList<string> exclusions, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            // Validate even excluded paths so a crafted archive cannot hide traversal entries.
            var target = SafePath(destination, normalized);
            if (normalized.EndsWith('/') || normalized.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                || exclusions.Any(prefix => normalized.StartsWith(prefix.Replace('\\', '/'), StringComparison.Ordinal))) continue;
            if (entry.Length > 512L * 1024 * 1024) throw new InvalidDataException("原生库压缩包中存在过大的文件。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(output);
        }
    }

    internal static void ValidateAssets(JsonElement metadata, string assetsDirectory, CancellationToken ct)
    {
        string? indexId = null;
        if (metadata.TryGetProperty("assetIndex", out var index)) indexId = index.GetProperty("id").GetString();
        else if (metadata.TryGetProperty("assets", out var assets)) indexId = assets.GetString();
        if (string.IsNullOrWhiteSpace(indexId)) throw new InvalidDataException("这个旧版本没有可识别的资源索引，第一版暂不支持启动。");
        var indexPath = SafePath(Path.Combine(assetsDirectory, "indexes"), indexId + ".json");
        if (!File.Exists(indexPath)) throw new FileNotFoundException($"缺少游戏资源索引 {indexId}。请先用已有启动器完整安装这个版本。", indexPath);
        using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("游戏资源索引不完整，请先补全游戏文件。");
        var virtualAssets = root.TryGetProperty("virtual", out var virtualFlag) && virtualFlag.ValueKind == JsonValueKind.True;
        var mapToResources = root.TryGetProperty("map_to_resources", out var mapFlag) && mapFlag.ValueKind == JsonValueKind.True;
        var checkedHashes = new HashSet<string>(StringComparer.Ordinal);
        var missing = 0;
        var missingLegacy = 0;
        var gameDirectory = Path.GetDirectoryName(assetsDirectory)!;
        foreach (var item in objects.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
            var hash = item.Value.GetProperty("hash").GetString() ?? "";
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("游戏资源索引包含不正确的文件哈希。");
            if (checkedHashes.Add(hash) && !File.Exists(SafePath(Path.Combine(assetsDirectory, "objects"), hash[..2] + "/" + hash))) missing++;
            if (virtualAssets && !File.Exists(SafePath(Path.Combine(assetsDirectory, "virtual", indexId), item.Name))) missingLegacy++;
            if (mapToResources && !File.Exists(SafePath(Path.Combine(gameDirectory, "resources"), item.Name))) missingLegacy++;
        }
        if (missing > 0) throw new InvalidDataException($"游戏还缺少 {missing} 个资源文件。第一版暂不自动下载，请先用已有启动器补全。");
        if (missingLegacy > 0) throw new InvalidDataException("这个旧版的 virtual/resources 资源尚未准备完整，请先用已有启动器运行一次该版本。");
    }
}
