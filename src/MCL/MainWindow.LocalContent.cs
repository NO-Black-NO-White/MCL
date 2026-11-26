using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    public bool CanManageSaves => !IsBusy && !IsGameRunning && !_launcher.IsRunning
        && !MultiplayerActive && SelectedVersion is { IsValid: true };
    public bool CanShareModpack => CanManageSaves && SelectedVersion?.ModpackName is not null;
    public bool CanCompareModpack => !IsBusy && (_runningGame ?? SelectedVersion) is { IsValid: true };

    private async void ExportModpack_Click(object sender, RoutedEventArgs e)
    {
        if (!CanShareModpack || SelectedVersion is not { } version) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出整合包分享 ZIP",
            Filter = "整合包 ZIP (*.zip)|*.zip",
            FileName = version.Id + "-share.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunBusyAsync(async () =>
        {
            StatusMessage = "正在打包整合包的模组、配置和资源包…";
            await LocalContentArchive.ExportPackAsync(GameDirectoryResolver.Normalize(GameDirectory), version, dialog.FileName, _lifetime.Token);
            StatusMessage = "分享包已导出。好友可将 ZIP 拖入 MCL 的「导入整合包」。";
        });
    }

    private async void CompareModpack_Click(object sender, RoutedEventArgs e)
    {
        var version = _runningGame ?? SelectedVersion;
        if (!CanCompareModpack || version is null) return;
        var dialog = new OpenFileDialog { Title = "选择好友的整合包分享 ZIP", Filter = "整合包 ZIP (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        await RunBusyAsync(async () =>
        {
            StatusMessage = "正在核对游戏版本、加载器和模组文件…";
            var result = await Task.Run(() => ModpackComparison.CompareAsync(Path.GetDirectoryName(Path.GetDirectoryName(version.VersionDirectory))!,
                version, dialog.FileName, _lifetime.Token), _lifetime.Token);
            if (_closing) return;
            StatusMessage = "整合包对比完成。";
            new Window
            {
                Owner = this,
                Title = $"对比 {version.DisplayName}",
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 610,
                Height = 400,
                Content = new TextBox
                {
                    Text = result,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(16)
                }
            }.ShowDialog();
        });
    }

    private async void BackupSaves_Click(object sender, RoutedEventArgs e)
    {
        if (!CanManageSaves || SelectedVersion is not { } version) return;
        var dialog = new SaveFileDialog
        {
            Title = version.ModpackName is null ? "备份此游戏目录共享的存档" : "备份此整合包的存档",
            Filter = "MCL 存档备份 (*.zip)|*.zip",
            FileName = $"MCL-saves-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunBusyAsync(async () =>
        {
            StatusMessage = "正在备份存档…";
            await LocalContentArchive.BackupSavesAsync(GameDirectoryResolver.Normalize(GameDirectory), version, dialog.FileName, _lifetime.Token);
            StatusMessage = $"存档备份已保存：{Path.GetFileName(dialog.FileName)}。";
        });
    }

    private async void RestoreSaves_Click(object sender, RoutedEventArgs e)
    {
        if (!CanManageSaves || SelectedVersion is not { } version) return;
        var dialog = new OpenFileDialog { Title = "选择 MCL 存档备份 ZIP", Filter = "MCL 存档备份 (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        var location = version.ModpackName is null ? "此游戏目录下多个原版共用的存档" : $"「{version.DisplayName}」的存档";
        if (MessageBox.Show(this, $"将用备份替换{location}。原存档文件夹会留在旁边，方便找回。\n\n现在恢复吗？",
            "恢复存档", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunBusyAsync(async () =>
        {
            StatusMessage = "正在验证和恢复存档…";
            var previous = await LocalContentArchive.RestoreSavesAsync(GameDirectoryResolver.Normalize(GameDirectory),
                version, dialog.FileName, _lifetime.Token);
            StatusMessage = previous is null ? "存档已恢复。" : $"存档已恢复。原存档保留在 {Path.GetFileName(previous)}。";
            if (previous is not null) _log.Info($"恢复前的存档目录：{previous}");
        });
    }
}
