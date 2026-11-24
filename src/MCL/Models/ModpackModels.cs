namespace MCL.Models;

public sealed record ModpackPreview(
    string ArchivePath, string ArchiveHash, string Name, string Version,
    string Minecraft, string? Fabric, int RequiredFiles, int OptionalFiles, long DownloadBytes)
{
    public string LoaderText => Fabric is null ? "原版" : $"Fabric {Fabric}";
}

public sealed record ModpackInstallResult(string InstanceId, string Name, int RequiredJava);

internal sealed record FabricLibrary(string Coordinate, string Sha1);
internal sealed record ModpackInstance(
    int Format, string Name, string PackVersion, string Minecraft, string BaseSha1,
    string? Fabric, FabricLibrary[] Libraries, string[] JvmArguments, bool UsesCachedBase = false);
