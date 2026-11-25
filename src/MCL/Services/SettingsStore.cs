using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public string DataDirectory { get; }

    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCL"));
        _settingsPath = Path.Combine(DataDirectory, "settings.json");
    }

    public LauncherSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_settingsPath)) return new LauncherSettings();
            try
            {
                using var stream = File.OpenRead(_settingsPath);
                if (stream.Length > 1024 * 1024) throw new JsonException("Settings file is too large.");
                var settings = JsonSerializer.Deserialize<LauncherSettings>(stream, JsonOptions) ?? throw new JsonException("Settings are empty.");
                if (string.IsNullOrWhiteSpace(settings.GameDirectory)) settings.GameDirectory = new LauncherSettings().GameDirectory;
                settings.JavaPath ??= "";
                if (settings.MemoryMb < 512 || settings.MemoryMb > 1_048_576) settings.MemoryMb = 4096;
                if (!MinecraftMetadata.IsValidLocalPlayerName(settings.PlayerName)) settings.PlayerName = "Player";
                return settings;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                var defaults = new LauncherSettings();
                try
                {
                    var backup = Path.Combine(DataDirectory, $"settings.corrupt.{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.{Guid.NewGuid():N}.json");
                    File.Move(_settingsPath, backup);
                    Save(defaults);
                }
                catch (Exception recoveryException) when (recoveryException is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // An unreadable directory must not prevent the launcher from opening.
                }
                return defaults;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return new LauncherSettings();
            }
        }
    }

    public void Save(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            var temporaryPath = Path.Combine(DataDirectory, $"settings.{Guid.NewGuid():N}.tmp");
            var completeTemporaryFile = false;
            try
            {
                Directory.CreateDirectory(DataDirectory);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, settings, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                completeTemporaryFile = true;

                // A file can be encrypted even if its parent folder is not. Keep that
                // protection when replacing it with a file created in this folder.
                if (File.Exists(_settingsPath)
                    && (File.GetAttributes(_settingsPath) & FileAttributes.Encrypted) != 0
                    && (File.GetAttributes(temporaryPath) & FileAttributes.Encrypted) == 0)
                {
                    File.Encrypt(temporaryPath);
                }

                // ReplaceFile's metadata merge can fail in EFS directories. A same-folder
                // rename with overwrite works there and never deletes the old file first.
                File.Move(temporaryPath, _settingsPath, overwrite: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException($"无法写入设置文件：{_settingsPath}\n请检查文件是否只读、是否被其他程序占用，以及配置文件夹的写入权限。", ex);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                throw new IOException($"设置文件正在被其他程序占用，暂时无法保存：{_settingsPath}\n请关闭占用程序后重试。", ex);
            }
            catch (IOException ex)
            {
                throw new IOException($"保存设置失败。配置文件位置：{_settingsPath}\n系统原因：{ex.Message}", ex);
            }
            finally
            {
                // Keep a completed temporary file if no destination remains, so an
                // interrupted first save does not discard its only complete copy.
                try
                {
                    if (File.Exists(temporaryPath) && (!completeTemporaryFile || File.Exists(_settingsPath)))
                        File.Delete(temporaryPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
        }
    }
}
