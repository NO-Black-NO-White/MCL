using System.Diagnostics;
using System.Windows;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private readonly VersionManagementService _versionManager;
    private bool _isDeletingVersion;
    public bool CanOpenVersionFolder => !IsBusy && SelectedVersion is not null;
    public bool CanDeleteVersion => !IsBusy && !_isDeletingVersion && !IsGameRunning && !_launcher.IsRunning && SelectedVersion is not null;
    public string ManagedVersionTitle => SelectedVersion?.DisplayName ?? "选择一个版本";
    public string ManagedVersionPath => SelectedVersion is { } version
        ? version.ModpackName is null ? version.VersionDirectory : Path.Combine(version.VersionDirectory, "game") : "";
    public string VersionManagementHint => IsGameRunning || _launcher.IsRunning ? "游戏运行中，退出游戏后可以删除版本。" : "删除前会显示确认窗口，所选版本文件夹将移入回收站。";

    private void NotifyVersionManagement()
    {
        Notify(nameof(CanOpenVersionFolder), nameof(CanDeleteVersion), nameof(ManagedVersionTitle), nameof(ManagedVersionPath), nameof(VersionManagementHint));
        if (VersionManagementCard is not null) VersionManagementCard.Visibility = SelectedVersion is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void DeleteSelectedVersion_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDeleteVersion || SelectedVersion is not { } version) return;
        var directory = GameDirectoryResolver.Normalize(GameDirectory);
        await RunBusyAsync(async () =>
        {
            var info = await _versionManager.InspectAsync(directory, version, _lifetime.Token);
            var dialog = new VersionDeleteDialog(info) { Owner = this };
            if (dialog.ShowDialog() != true) { StatusMessage = "已取消删除版本。"; return; }
            _isDeletingVersion = true;
            NotifyVersionManagement();
            StatusMessage = $"正在将版本 {version.Id} 移入回收站…";
            try
            {
                await _versionManager.DeleteAsync(info, _lifetime.Token);
                await ScanVersionsAsync();
                try
                {
                    SaveCurrentSettings();
                    StatusMessage = $"版本 {version.Id} 已移入回收站，版本列表已刷新。";
                }
                catch (Exception ex)
                {
                    _log.Warn("版本已移除，但启动设置未保存：" + ex.Message);
                    StatusMessage = "版本已移入回收站，启动设置暂未保存，可在设置中重试。";
                }
                _log.Info($"已将版本 {version.Id} 移入回收站。");
            }
            finally { _isDeletingVersion = false; NotifyVersionManagement(); }
        });
    }

    private void OpenVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenVersionFolder || SelectedVersion is not { } version) return;
        try
        {
            var root = GameDirectoryResolver.Normalize(GameDirectory);
            var expected = Path.GetFullPath(Path.Combine(root, "versions", version.Id));
            if (!string.Equals(Path.GetDirectoryName(expected), Path.Combine(root, "versions"), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(expected, Path.GetFullPath(version.VersionDirectory), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("版本目录已改变，请刷新列表后重试。");
            var folder = version.ModpackName is null ? expected : ModpackPaths.SafeTarget(expected, "game");
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("版本文件夹已不存在，请刷新列表。");
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { ReportError("无法打开版本文件夹", ex); }
    }

    public void OpenVersions() => VersionsNav.IsChecked = true;
}
