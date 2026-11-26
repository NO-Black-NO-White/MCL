using System.Windows;
using System.Windows.Controls;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private RecentGameLaunch[] _recentLaunches = [];
    private SavePreview[] _homeSaves = [];
    private SavePreview? _selectedHomeSave;
    private (string Root, string? Instance)? _homeSaveScope;
    private CancellationTokenSource? _homeSaveCancellation;
    private string _homeSavesMessage = "选择一个已安装的版本来查看存档。";

    public IReadOnlyList<SavePreview> HomeSaves => _homeSaves;
    public bool HasHomeSave => SelectedHomeSave is not null;
    public bool HasMultipleHomeSaves => _homeSaves.Length > 1;
    public string HomeSaveCount => _homeSaves.Length == 0 ? "" :
        $"{(_homeSaveScope?.Instance is null ? "共用存档" : "独立存档")} · {_homeSaves.Length} 个";
    public string HomeSavesMessage { get => _homeSavesMessage; private set => Set(ref _homeSavesMessage, value); }
    public SavePreview? SelectedHomeSave
    {
        get => _selectedHomeSave;
        set { if (Set(ref _selectedHomeSave, value)) Notify(nameof(HasHomeSave)); }
    }

    internal async Task RefreshHomeSavesAsync()
    {
        _homeSaveCancellation?.Cancel();
        if (_closing || _currentPage != "Home") return;
        if (SelectedVersion is not { } version)
        {
            _homeSaveScope = null;
            SetHomeSaves([]);
            HomeSavesMessage = "选择一个已安装的版本来查看存档。";
            return;
        }

        var scope = (Root: GameDirectory, Instance: version.ModpackName is null ? null : version.Id);
        if (_homeSaveScope != scope)
        {
            _homeSaveScope = scope;
            SetHomeSaves([]);
        }
        if (_homeSaves.Length == 0) HomeSavesMessage = "正在读取存档…";
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _homeSaveCancellation = scan;
        try
        {
            var saves = await Task.Run(() => SavePreviewReader.Read(GameDirectoryResolver.Normalize(scope.Root), version, scan.Token), scan.Token);
            if (scan.IsCancellationRequested) return;
            SetHomeSaves(saves);
            HomeSavesMessage = "此游戏目录还没有单人存档。";
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (scan.IsCancellationRequested) return;
            SetHomeSaves([]);
            HomeSavesMessage = "暂时无法读取存档，请检查游戏文件夹。";
        }
        finally
        {
            if (ReferenceEquals(_homeSaveCancellation, scan)) _homeSaveCancellation = null;
        }
    }

    private void SetHomeSaves(SavePreview[] saves)
    {
        if (_homeSaves.SequenceEqual(saves)) return;
        var folder = SelectedHomeSave?.Folder;
        _homeSaves = saves;
        Notify(nameof(HomeSaves), nameof(HomeSaveCount), nameof(HasMultipleHomeSaves));
        SelectedHomeSave = saves.FirstOrDefault(save => save.Folder == folder) ?? saves.FirstOrDefault();
    }

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
