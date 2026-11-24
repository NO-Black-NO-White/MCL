namespace MCL.Models;

public sealed record VersionDeletionInfo(string GameDirectory, GameVersion Version, string DirectoryPath, DateTime CreationTimeUtc, IReadOnlyList<string> LocalDataKinds)
{
    public string Title => $"删除版本 {Version.Id}？";
    public string DataNotice => LocalDataKinds.Count == 0
        ? "该版本文件夹及其中的文件会一起移入 Windows 回收站。"
        : $"该文件夹还包含{string.Join("、", LocalDataKinds)}，这些内容也会随版本一起移入回收站。";
}
