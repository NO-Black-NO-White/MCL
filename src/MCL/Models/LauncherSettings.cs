namespace MCL.Models;

public sealed class LauncherSettings
{
    public string GameDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public string PlayerName { get; set; } = "Player";
    public string? SelectedVersionId { get; set; }
    public string MicrosoftClientId { get; set; } = "";
    public bool UseMicrosoftAccount { get; set; }
    public string RelayEndpoint { get; set; } = "";
}
