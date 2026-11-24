namespace MCL.Models;

public sealed record RemoteGameVersion(string Id, string Type, DateTimeOffset ReleaseTime, Uri MetadataUrl, string MetadataSha1)
{
    public string ReleaseDateText => ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd");
    public string KindText => "原版正式版";
}

public sealed record VersionInstallInfo(RemoteGameVersion Version, int RequiredJava, long EstimatedBytes, byte[] Metadata);

public sealed record InstallProgress(string Stage, string Detail, int CompletedFiles, int TotalFiles, long CompletedBytes, long TotalBytes, double BytesPerSecond)
{
    public double Percent => TotalBytes > 0 ? Math.Clamp(100d * CompletedBytes / TotalBytes, 0, 100) : 0;
}

public sealed record InstallResult(string VersionId, int DownloadedFiles, int ReusedFiles, int RequiredJava);

internal sealed record DownloadFile(Uri Url, string Path, string Sha1, long Size);
