namespace MCL.Models;

public sealed record JavaInstallation(string Path, int MajorVersion, string VersionText, bool Is64Bit);
