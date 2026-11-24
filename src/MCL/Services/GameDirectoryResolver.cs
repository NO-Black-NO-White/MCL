namespace MCL.Services;

public static class GameDirectoryResolver
{
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(full)).Equals("versions", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full)) is { } parent
            && (Directory.Exists(Path.Combine(parent, "assets")) || Directory.Exists(Path.Combine(parent, "libraries")))) return parent;
        if (!Directory.Exists(Path.Combine(full, "versions")) && Directory.Exists(Path.Combine(full, ".minecraft", "versions"))) return Path.Combine(full, ".minecraft");
        return full;
    }
}
