namespace MCL.Models;

public sealed record LogEntry(DateTime Time, string Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public override string ToString() => $"[{Time:yyyy-MM-dd HH:mm:ss}] [{Level}] {Message}";
}
