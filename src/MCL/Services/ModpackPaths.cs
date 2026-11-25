using System.Text.RegularExpressions;
using MCL.Models;

namespace MCL.Services;

internal static partial class ModpackPaths
{
    internal const string InstanceKey = "mclModpack";
    internal const string FabricMain = "net.fabricmc.loader.impl.launch.knot.KnotClient";
    internal const string LegacyFabricMain = "net.fabricmc.loader.launch.knot.KnotClient";
    internal const string VanillaMain = "net.minecraft.client.main.Main";
    internal const string ForgeMain = "cpw.mods.bootstraplauncher.BootstrapLauncher";
    internal static bool IsForgeVersion(string? minecraft, string? forge) => minecraft == "1.20.1"
        && IsLoaderVersion(forge) && forge!.StartsWith("47.", StringComparison.Ordinal);
    internal static bool IsGameVersion(string? value) => value is { Length: <= 32 }
        && GameVersionPattern().IsMatch(value) && Version.TryParse(value, out var v)
        && v.Major >= 1 && (v.Major != 1 || v.Minor >= 13);
    internal static bool IsLoaderVersion(string? value) => value is { Length: <= 32 } && GameVersionPattern().IsMatch(value);
    internal static bool IsHex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    internal static bool IsAllowedJvmArgument(string argument) => argument is
        "-XX:+UnlockDiagnosticVMOptions" or "-XX:-UseBASE64Intrinsics" or "-DFabricMcEmu= net.minecraft.client.main.Main ";

    internal static string BaseDirectory(string version, string sha1, bool cached) =>
        cached ? $".mcl-base-versions/{version}/{sha1.ToLowerInvariant()}" : $"versions/{version}";

    internal static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\\'))
            throw new InvalidDataException("整合包含有无效的文件路径。");
        foreach (var part in value.Split('/'))
        {
            if (part.Length == 0 || part.Length > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Any(char.IsControl) || DeviceName().IsMatch(part))
                throw new InvalidDataException("整合包含有无效的文件路径。");
        }
        return value;
    }

    internal static string SafeTarget(string root, string relative)
    {
        var target = MinecraftMetadata.SafePath(root, Relative(relative));
        for (string? path = target; path is not null; path = Path.GetDirectoryName(path))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("整合包目录不能使用符号链接或目录联接。请选择普通文件夹。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return target;
    }

    internal static string LibraryPath(string coordinate)
    {
        if (coordinate.Length > 160 || !CoordinatePattern().IsMatch(coordinate))
            throw new InvalidDataException("Fabric 依赖坐标无效。");
        return Relative(MinecraftMetadata.MavenPath(coordinate));
    }

    internal static void CheckInstance(string id, string? parent, string? mainClass, ModpackInstance data)
    {
        if (!id.StartsWith("pack-", StringComparison.Ordinal) || data.Format != 1 || string.IsNullOrWhiteSpace(data.Name)
            || data.Name.Length > 120 || data.Name.Any(char.IsControl) || !IsGameVersion(data.Minecraft)
            || parent != (data.UsesCachedBase ? null : data.Minecraft) || !IsHex(data.BaseSha1, 40)
            || data.Libraries is null || data.JvmArguments is null)
            throw new InvalidDataException("整合包实例配置无效，请重新导入。");
        if (data.Forge is not null)
        {
            if (!IsForgeVersion(data.Minecraft, data.Forge) || data.Fabric is not null || mainClass != ForgeMain
                || !IsHex(data.ForgeProfileSha256, 64) || data.Libraries.Length != 0 || data.JvmArguments.Length != 0)
                throw new InvalidDataException("Forge 实例配置无效，请重新导入。");
            return;
        }
        if (data.ForgeProfileSha256 is not null) throw new InvalidDataException("整合包加载器配置无效。");
        if (data.Fabric is null)
        {
            if (mainClass != VanillaMain || data.Libraries.Length != 0 || data.JvmArguments.Length != 0)
                throw new InvalidDataException("原版整合包配置无效，请重新导入。");
            return;
        }
        if (!IsLoaderVersion(data.Fabric) || mainClass is not (FabricMain or LegacyFabricMain)
            || data.Libraries.Length is < 1 or > 64 || data.JvmArguments.Length > 3
            || !data.JvmArguments.All(IsAllowedJvmArgument))
            throw new InvalidDataException("Fabric 实例配置无效，请重新导入。");
        foreach (var library in data.Libraries)
        {
            if (library is null || library.Coordinate is null || !IsHex(library.Sha1, 40))
                throw new InvalidDataException("Fabric 实例缺少依赖校验信息。");
            _ = LibraryPath(library.Coordinate);
        }
    }

    [GeneratedRegex(@"\A\d+\.\d+(?:\.\d+){0,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex GameVersionPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*:[A-Za-z0-9_.-]+:[A-Za-z0-9_.+\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex CoordinatePattern();

    [GeneratedRegex(@"\A(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();
}
