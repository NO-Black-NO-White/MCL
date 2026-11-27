namespace MCL.Services;

internal enum LaunchFix { Logs, Java, Repair, Memory, Modpack, Settings }

internal sealed record LaunchProblem(string Message, LaunchFix Fix, int? JavaMajor = null);

internal sealed class GameLaunchProblemException(string message, LaunchFix fix,
    int? javaMajor = null, Exception? inner = null) : IOException(message, inner)
{
    internal LaunchProblem Problem { get; } = new(message, fix, javaMajor);
}

internal static class LaunchTroubleshooter
{
    internal static LaunchProblem? FromOutput(string line)
    {
        if (line.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase))
            return new("游戏或模组需要更高版本的 Java。请先核对这个版本的 Java 要求。", LaunchFix.Java);
        if (line.Contains("Could not reserve enough space", StringComparison.OrdinalIgnoreCase)
            || line.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase))
            return new("Java 无法获得足够的内存。请检查分配内存和系统可用内存。", LaunchFix.Memory);
        if (line.Contains("Invalid or corrupt jarfile", StringComparison.OrdinalIgnoreCase))
            return new("Java 报告存在损坏的 JAR。可以先检查官方游戏文件。", LaunchFix.Repair);
        if (line.Contains("Incompatible mods found", StringComparison.OrdinalIgnoreCase)
            || line.Contains("ModLoadingException", StringComparison.OrdinalIgnoreCase))
            return new("模组加载失败。请在运行日志中核对模组版本和依赖。", LaunchFix.Modpack);
        return null;
    }
}
