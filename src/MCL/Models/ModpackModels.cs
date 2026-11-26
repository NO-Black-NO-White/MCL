namespace MCL.Models;

public sealed record ModpackPreview(
    string ArchivePath, string ArchiveHash, string Name, string Version,
    string Minecraft, string? Fabric, int RequiredFiles, int OptionalFiles, long DownloadBytes,
    string? Forge = null, bool CurseForge = false, bool MclShare = false)
{
    public string LoaderText => Forge is not null ? $"Forge {Forge}" : Fabric is null ? "原版" : $"Fabric {Fabric}";
}

public sealed record ModpackInstallResult(string InstanceId, string Name, int RequiredJava);

internal sealed record FabricLibrary(string Coordinate, string Sha1);
internal sealed record FabricProfile(string MainClass, FabricLibrary[] Libraries, string[] Jvm);
internal sealed record LocalAppearanceLaunch(string ModJar, string? EmptyModsFolder, string? FabricFolder, FabricProfile? Fabric);
internal sealed record ModpackInstance(
    int Format, string Name, string PackVersion, string Minecraft, string BaseSha1,
    string? Fabric, FabricLibrary[] Libraries, string[] JvmArguments, bool UsesCachedBase = false,
    string? Forge = null, string? ForgeProfileSha256 = null);
