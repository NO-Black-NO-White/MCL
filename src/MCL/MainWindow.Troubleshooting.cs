using System.Windows;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private CancellationTokenSource? _repairCancellation;
    private (string Directory, LaunchProblem Problem)? _launchFailure;
    private string _repairStatus = "";
    private bool _repairIndeterminate;
    private double _repairPercent;

    private static bool SupportsRepair(GameVersion version) => version.ModpackName is not null
        || version.Kind == "原版" && version.InheritsFrom is null;
    public bool CanRepairVersion => !IsBusy && !IsGameRunning && !_launcher.IsRunning
        && SelectedVersion is { } version && SupportsRepair(version);
    public bool IsRepairing => _repairCancellation is not null;
    public string RepairStatus { get => _repairStatus; private set => Set(ref _repairStatus, value); }
    public bool RepairIndeterminate { get => _repairIndeterminate; private set => Set(ref _repairIndeterminate, value); }
    public double RepairPercent { get => _repairPercent; private set => Set(ref _repairPercent, value); }

    private LaunchProblem? SelectedProblem => _launchFailure is { } failure
        && failure.Directory == SelectedVersion?.VersionDirectory ? failure.Problem : null;
    public bool ShowLaunchProblem => SelectedProblem is not null && _currentPage is "Home" or "Versions" or "Multiplayer";
    public string LaunchProblemText => SelectedProblem?.Message ?? "";
    public string LaunchProblemAction => SelectedProblem switch
    {
        { Fix: LaunchFix.Java, JavaMajor: { } major } => $"安装 Java {major}",
        { Fix: LaunchFix.Java or LaunchFix.Memory } => "打开启动设置",
        { Fix: LaunchFix.Settings } => "选择游戏文件夹",
        { Fix: LaunchFix.Repair } when SelectedVersion is { } version && SupportsRepair(version) => "检查并修复",
        _ => "查看运行日志"
    };

    private void NotifyLaunchProblem() => Notify(nameof(ShowLaunchProblem), nameof(LaunchProblemText), nameof(LaunchProblemAction));

    private void OnGameFailed(GameVersion version, LaunchProblem problem)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnGameFailed(version, problem)); return; }
        _launchFailure = (version.VersionDirectory, problem);
        NotifyLaunchProblem();
    }

    private void DismissLaunchProblem_Click(object sender, RoutedEventArgs e)
    {
        _launchFailure = null;
        NotifyLaunchProblem();
    }

    private async void LaunchProblemAction_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || SelectedVersion is not { } version || SelectedProblem is not { } problem) return;
        if (problem is { Fix: LaunchFix.Java, JavaMajor: { } major })
            await InstallJavaForVersionAsync(version, major);
        else if (problem.Fix is LaunchFix.Java or LaunchFix.Memory)
            SettingsNav.IsChecked = true;
        else if (problem.Fix == LaunchFix.Settings)
            BrowseGame_Click(sender, e);
        else if (problem.Fix == LaunchFix.Repair && SupportsRepair(version))
        {
            OpenVersions();
            await RepairSelectedVersionAsync();
        }
        else LogsNav.IsChecked = true;
    }

    private async void RepairVersion_Click(object sender, RoutedEventArgs e) => await RepairSelectedVersionAsync();
    private void CancelRepair_Click(object sender, RoutedEventArgs e) => _repairCancellation?.Cancel();

    private async Task RepairSelectedVersionAsync()
    {
        if (!CanRepairVersion || SelectedVersion is not { } version) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _repairCancellation = cancellation;
        Notify(nameof(IsRepairing));
        try
        {
            await RunBusyAsync(async () =>
            {
                RepairIndeterminate = true;
                RepairPercent = 0;
                RepairStatus = "正在核对官方游戏文件…";
                try
                {
                    var directory = GameDirectoryResolver.Normalize(GameDirectory);
                    var progress = new Progress<InstallProgress>(update =>
                    {
                        if (_closing || cancellation.IsCancellationRequested || !ReferenceEquals(_repairCancellation, cancellation)) return;
                        RepairStatus = update.TotalFiles == 0 ? update.Stage
                            : $"{update.Stage} · {update.CompletedFiles:N0} / {update.TotalFiles:N0} 个文件";
                        RepairIndeterminate = update.TotalFiles == 0;
                        RepairPercent = update.TotalFiles > 0 ? 100d * update.CompletedFiles / update.TotalFiles : 0;
                    });
                    var result = await _downloads.RepairAsync(version, directory, progress, cancellation.Token);
                    await ScanVersionsAsync(version.Id);
                    if (_launchFailure is { } failure && failure.Directory == version.VersionDirectory && failure.Problem.Fix == LaunchFix.Repair)
                    {
                        _launchFailure = null;
                        NotifyLaunchProblem();
                    }
                    StatusMessage = result.DownloadedFiles == 0 ? "检查完成，官方游戏文件完整。"
                        : $"已补齐或修复 {result.DownloadedFiles:N0} 个文件，复用 {result.ReusedFiles:N0} 个完整文件。";
                    _log.Info($"已检查 {version.Id}。{StatusMessage}");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                { if (!_closing) StatusMessage = "已取消检查。已修复的文件会保留。"; }
                catch (Exception ex)
                { if (!_closing) ReportError("游戏修复未完成", ex); }
            });
        }
        finally
        {
            _repairCancellation = null;
            Notify(nameof(IsRepairing));
        }
    }
}
