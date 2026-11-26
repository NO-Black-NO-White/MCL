using System.Windows;
using System.Windows.Controls;
using MCL.Models;

namespace MCL;

public partial class MainWindow
{
    private RecentGameLaunch[] _recentLaunches = [];

    public sealed record RecentGameItem(GameVersion Version, string LastLaunch);

    public IReadOnlyList<RecentGameItem> RecentGames => _recentLaunches
        .Where(launch => string.Equals(launch.GameDirectory, GameDirectory, StringComparison.OrdinalIgnoreCase))
        .Select(launch => (Version: Versions.FirstOrDefault(version => version.IsValid && version.Id == launch.VersionId), launch.LaunchedAt))
        .Where(item => item.Version is not null)
        .Take(3)
        .Select(item => new RecentGameItem(item.Version!, item.LaunchedAt.ToLocalTime().ToString("MM-dd HH:mm")))
        .ToArray();

    private void RememberLaunch(string directory, string versionId)
    {
        var launch = new RecentGameLaunch(directory, versionId, DateTimeOffset.Now);
        _recentLaunches = _recentLaunches
            .Where(entry => !string.Equals(entry.GameDirectory, directory, StringComparison.OrdinalIgnoreCase) || entry.VersionId != versionId)
            .Prepend(launch).Take(24).ToArray();
        Notify(nameof(RecentGames));
        try { SaveCurrentSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _log.Warn($"最近启动记录暂未保存：{ex.Message}"); }
    }

    private void ChooseRecentVersion_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInteract || sender is not Button { DataContext: RecentGameItem item } || !Versions.Contains(item.Version)) return;
        SelectedVersion = item.Version;
    }
}
