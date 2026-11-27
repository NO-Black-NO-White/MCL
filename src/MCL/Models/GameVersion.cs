namespace MCL.Models;

public sealed record GameVersion(string Id, string JsonPath, string VersionDirectory, string Kind, string? InheritsFrom, bool IsValid, string? Issue)
{
    public string? ModpackName { get; init; }
    public string? ModpackMinecraft { get; init; }
    public string? ModpackVersion { get; init; }
    public string? ModpackLoader { get; init; }
    public int RequiredJava { get; init; } = 8;
    public string DisplayName => ModpackName ?? Id;
    public string StatusText => IsValid ? "已发现" : "需要检查";
    public string BadgeText => !IsValid ? "需检查"
        : ModpackLoader is { Length: > 0 } loader ? loader.Split(' ')[0]
        : ModpackName is not null ? "整合包"
        : Kind;
    public string BadgeKind => !IsValid ? "warning"
        : ModpackName is not null || Kind != "原版" ? "modpack"
        : "vanilla";
    public string Description => Issue ?? (ModpackMinecraft is not null ? $"Minecraft {ModpackMinecraft} · 独立游戏目录"
        : InheritsFrom is null ? "本地游戏版本" : $"继承自 {InheritsFrom}");
}
