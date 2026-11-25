using System.Text;
using System.Text.RegularExpressions;
using MCL.Models;

namespace MCL.Services;

public sealed class LogService
{
    private readonly object _gate = new();
    private static readonly Regex ArgumentSecrets = new(@"(?i)(--(?:accessToken|refreshToken|clientToken|password|identityToken)\s+)(?:""[^""]*""|'[^']*'|\S+)", RegexOptions.Compiled);
    private static readonly Regex NamedSecrets = new(@"(?i)([""']?(?:access_?token|refresh_?token|client_?token|identity_?token|password|authorization)[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;&}]+)", RegexOptions.Compiled);
    private static readonly Regex BearerSecrets = new(@"(?i)\bBearer\s+[^\s""',;]+", RegexOptions.Compiled);

    public event Action<LogEntry>? EntryAdded;
    public string LogFilePath { get; }

    public LogService(string? dataDirectory = null)
    {
        var root = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCL");
        LogFilePath = Path.Combine(Path.GetFullPath(root), "logs", $"launcher-{DateTime.Now:yyyy-MM-dd}.log");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var sanitized = Redact(message ?? "");
        var entry = new LogEntry(DateTime.Now, level?.ToUpperInvariant() switch { "WARN" => "WARN", "ERROR" => "ERROR", "DEBUG" => "DEBUG", _ => "INFO" }, sanitized);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                using var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(entry.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Logging is best effort; an unavailable disk must not crash the UI.
            }
        }
        var handlers = EntryAdded;
        if (handlers is null) return;
        foreach (Action<LogEntry> handler in handlers.GetInvocationList())
        {
            try { handler(entry); }
            catch (Exception) { /* A subscriber failure must not break logging or game output processing. */ }
        }
    }

    private static string Redact(string message)
    {
        var text = message.Length > 65_536 ? message[..65_536] : message;
        text = BearerSecrets.Replace(text, "Bearer [已隐藏]");
        text = ArgumentSecrets.Replace(text, "$1[已隐藏]");
        text = NamedSecrets.Replace(text, "$1[已隐藏]");
        text = text.Replace("\r", "\\r").Replace("\n", "\\n");
        return text.Length > 16_384 ? text[..16_384] + "…" : text;
    }
}
