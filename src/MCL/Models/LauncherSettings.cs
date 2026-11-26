namespace MCL.Models;

public sealed class LauncherSettings
{
    public string GameDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    public string JavaPath { get; set; } = "";
    public bool AutoMatchJava { get; set; } = true;
    public int MemoryMb { get; set; } = 4096;
    public string PlayerName { get; set; } = "Player";
    public string? SelectedVersionId { get; set; }
    public RecentGameLaunch[] RecentLaunches { get; set; } = [];
    public string MicrosoftClientId { get; set; } = "";
    public string RelayEndpoint { get; set; } = "";
    public bool ApplyLocalAppearance { get; set; }
    public string? LocalSkin { get; set; }
    public string? LocalCape { get; set; }
    public string LocalSkinModel { get; set; } = "default";
}

public sealed record RecentGameLaunch(string GameDirectory, string VersionId, DateTimeOffset LaunchedAt);
