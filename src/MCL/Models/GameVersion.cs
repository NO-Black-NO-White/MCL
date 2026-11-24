namespace MCL.Models;

public sealed record GameVersion(string Id, string JsonPath, string VersionDirectory, string Kind, string? InheritsFrom, bool IsValid, string? Issue)
{
    public string StatusText => IsValid ? "已发现" : "需要检查";
    public string Description => Issue ?? (InheritsFrom is null ? "本地游戏版本" : $"继承自 {InheritsFrom}");
}
