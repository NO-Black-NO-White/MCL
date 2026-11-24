namespace MCL.Models;

public sealed record GameVersion(string Id, string JsonPath, string VersionDirectory, string Kind, string? InheritsFrom, bool IsValid, string? Issue)
{
    public string? ModpackName { get; init; }
    public string? ModpackMinecraft { get; init; }
    public string? ModpackVersion { get; init; }
    public string? ModpackLoader { get; init; }
    public string DisplayName => ModpackName ?? Id;
    public string StatusText => IsValid ? "已发现" : "需要检查";
    public string Description => Issue ?? (ModpackMinecraft is not null ? $"Minecraft {ModpackMinecraft} · 独立游戏目录"
        : InheritsFrom is null ? "本地游戏版本" : $"继承自 {InheritsFrom}");
}
