using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private readonly MinecraftDownloadService _downloads = new();
    private readonly bool _offlinePreview;
    private IReadOnlyList<RemoteGameVersion> _allRemoteVersions = [];
    private RemoteGameVersion? _selectedRemote;
    private VersionInstallInfo? _selectedInstallInfo;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _installCancellation;
    private bool _isLoadingVersionInfo;
    private bool _isInstalling;
    private bool _catalogLoaded;
    private string _versionSearch = "";
    private string _remoteJava = "选择版本后查看运行要求";
    private string _remoteSize = "—";
    private string _installStage = "准备下载";
    private string _installDetail = "选择一个原版版本，MCL 会下载并校验它所需的全部文件。";
    private string _installFiles = "";
    private string _installTransfer = "";
    private string _installSpeed = "";
    private double _installPercent;
    private bool _installIndeterminate;

    public ObservableCollection<RemoteGameVersion> AvailableVersions { get; } = [];
    public RemoteGameVersion? SelectedRemoteVersion
    {
        get => _selectedRemote;
        set { if (Set(ref _selectedRemote, value)) { _selectedInstallInfo = null; Notify(nameof(RemoteVersionTitle), nameof(CanInstall)); } }
    }
    public string VersionSearch { get => _versionSearch; set { if (Set(ref _versionSearch, value)) FilterRemoteVersions(); } }
    public string RemoteVersionTitle => SelectedRemoteVersion?.Id ?? "选择一个版本";
    public string RemoteJava { get => _remoteJava; private set => Set(ref _remoteJava, value); }
    public string RemoteSize { get => _remoteSize; private set => Set(ref _remoteSize, value); }
    public string RemoteCountText => $"{AvailableVersions.Count} 个正式版 · 官方源";
    public bool IsInstalling { get => _isInstalling; private set { if (Set(ref _isInstalling, value)) NotifyDownloadActions(); } }
    public bool CanInstall => !IsBusy && !IsInstalling && !_isLoadingVersionInfo && _selectedInstallInfo is not null;
    public bool CanCancelInstall => IsInstalling && _installCancellation is { IsCancellationRequested: false };
    public string InstallButtonText => IsInstalling ? "正在安装…" : InstallStage is "安装失败" or "已取消" ? "重新尝试安装" : "下载并安装";
    public string InstallStage { get => _installStage; private set { if (Set(ref _installStage, value)) Notify(nameof(InstallButtonText)); } }
    public string InstallDetail { get => _installDetail; private set => Set(ref _installDetail, value); }
    public string InstallFiles { get => _installFiles; private set => Set(ref _installFiles, value); }
    public string InstallTransfer { get => _installTransfer; private set => Set(ref _installTransfer, value); }
    public string InstallSpeed { get => _installSpeed; private set => Set(ref _installSpeed, value); }
    public double InstallPercent { get => _installPercent; private set => Set(ref _installPercent, value); }
    public bool InstallIndeterminate { get => _installIndeterminate; private set => Set(ref _installIndeterminate, value); }

    private void NotifyDownloadActions() => Notify(nameof(CanInstall), nameof(CanCancelInstall), nameof(InstallButtonText), nameof(ActivityText));

    private void FilterRemoteVersions()
    {
        var previous = SelectedRemoteVersion?.Id;
        AvailableVersions.Clear();
        foreach (var version in _allRemoteVersions.Where(item => item.Id.Contains(VersionSearch.Trim(), StringComparison.OrdinalIgnoreCase))) AvailableVersions.Add(version);
        SelectedRemoteVersion = AvailableVersions.FirstOrDefault(item => item.Id == previous) ?? AvailableVersions.FirstOrDefault();
        EmptyRemote.Visibility = AvailableVersions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Notify(nameof(RemoteCountText));
    }

    private async Task LoadRemoteVersionsAsync()
    {
        if (IsBusy || IsInstalling || _offlinePreview) return;
        await RunBusyAsync(async () =>
        {
            StatusMessage = "正在读取官方版本列表…";
            _allRemoteVersions = await _downloads.GetVersionsAsync(_lifetime.Token);
            _catalogLoaded = true;
            FilterRemoteVersions();
            StatusMessage = $"已读取 {_allRemoteVersions.Count} 个原版正式版。选择版本后可以下载安装。";
        });
    }

    private async void RemoteVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _previewCancellation?.Cancel();
        if (SelectedRemoteVersion is not { } version)
        {
            _selectedInstallInfo = null;
            _isLoadingVersionInfo = false;
            RemoteJava = "选择版本后查看运行要求";
            RemoteSize = "—";
            NotifyDownloadActions();
            return;
        }
        RemoteVersionList.ScrollIntoView(version);
        if (_offlinePreview || IsInstalling) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCancellation = cancellation;
        _isLoadingVersionInfo = true;
        RemoteJava = "正在检查 Java 要求…";
        RemoteSize = "正在读取…";
        NotifyDownloadActions();
        try
        {
            var info = await _downloads.InspectVersionAsync(version, cancellation.Token);
            if (cancellation.IsCancellationRequested || SelectedRemoteVersion?.Id != version.Id) return;
            _selectedInstallInfo = info;
            RemoteJava = $"Java {info.RequiredJava} · 64 位";
            if (_java is null) RemoteJava += "（启动前需配置）";
            else if (_java.MajorVersion != info.RequiredJava) RemoteJava += $"（当前 {_java.MajorVersion}{(_java.MajorVersion < info.RequiredJava ? "，需切换" : "")}）";
            RemoteSize = FormatBytes(info.EstimatedBytes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (SelectedRemoteVersion?.Id == version.Id) { RemoteJava = "版本信息读取失败"; RemoteSize = "—"; StatusMessage = "读取版本信息失败，请点击刷新重试。"; _log.Error($"读取版本 {version.Id} 失败：{ex}"); }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) { _isLoadingVersionInfo = false; _previewCancellation = null; NotifyDownloadActions(); }
            cancellation.Dispose();
        }
    }

    private async void RefreshRemote_Click(object sender, RoutedEventArgs e) => await LoadRemoteVersionsAsync();
    private void ShowDownloads_Click(object sender, RoutedEventArgs e) => DownloadsNav.IsChecked = true;

    private async void BrowseInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new OpenFolderDialog { Title = "选择游戏安装文件夹", Multiselect = false };
        if (Directory.Exists(GameDirectory)) dialog.InitialDirectory = GameDirectory;
        if (dialog.ShowDialog(this) != true) return;
        GameDirectory = GameDirectoryResolver.Normalize(dialog.FolderName);
        await RunBusyAsync(async () => { await ScanVersionsAsync(); SaveCurrentSettings(); StatusMessage = "游戏安装位置已保存。"; });
    }

    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInstall || SelectedRemoteVersion is not { } version) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _installCancellation = cancellation;
        IsInstalling = true;
        IsBusy = true;
        InstallIndeterminate = true;
        InstallPercent = 0;
        InstallStage = "准备安装";
        InstalledShortcut.Visibility = Visibility.Collapsed;
        try
        {
            var directory = GameDirectoryResolver.Normalize(GameDirectory);
            GameDirectory = directory;
            var progress = new Progress<InstallProgress>(update =>
            {
                if (_closing || !ReferenceEquals(_installCancellation, cancellation)) return;
                InstallStage = update.Stage;
                InstallDetail = update.Detail;
                InstallPercent = update.Percent;
                InstallIndeterminate = update.TotalBytes == 0;
                InstallFiles = update.TotalFiles > 0 ? $"{update.CompletedFiles:N0} / {update.TotalFiles:N0} 个文件" : "";
                InstallTransfer = update.TotalBytes > 0 ? $"{FormatBytes(update.CompletedBytes)} / {FormatBytes(update.TotalBytes)}" : "";
                InstallSpeed = update.BytesPerSecond > 0 ? FormatBytes((long)update.BytesPerSecond) + "/s" : "";
            });
            var result = await _downloads.InstallAsync(version, directory, progress, cancellation.Token);
            await ScanVersionsAsync(result.VersionId);
            try { SaveCurrentSettings(); }
            catch (Exception ex) { _log.Warn("游戏已安装，但启动设置未能保存：" + ex.Message); }
            InstallStage = "安装完成";
            InstallIndeterminate = false;
            InstallPercent = 100;
            InstallDetail = $"{result.VersionId} 已加入已安装列表。下载 {result.DownloadedFiles} 个文件，复用 {result.ReusedFiles} 个文件。";
            StatusMessage = _java is null || _java.MajorVersion < result.RequiredJava ? $"安装完成。启动前请在设置中选择 Java {result.RequiredJava} 或兼容版本。"
                : _java.MajorVersion != result.RequiredJava ? $"安装完成，已设为当前版本。此版本推荐 Java {result.RequiredJava}，可在启动设置中切换。" : "安装完成，已设为当前版本，可以返回首页启动。";
            InstalledShortcut.Visibility = Visibility.Visible;
            _log.Info($"原版 {result.VersionId} 安装完成，下载 {result.DownloadedFiles}，复用 {result.ReusedFiles}。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            InstallStage = "已取消";
            InstallDetail = "已完成且校验通过的文件会保留，再次安装时可以复用。";
            StatusMessage = "下载已取消，可重新尝试安装。";
            _log.Info($"已取消安装 {version.Id}。");
        }
        catch (Exception ex)
        {
            InstallStage = "安装失败";
            InstallDetail = ex.Message;
            StatusMessage = "安装未完成，请查看提示后重新尝试。";
            _log.Error($"安装 {version.Id} 失败：{ex}");
        }
        finally
        {
            _installCancellation = null;
            InstallIndeterminate = false;
            IsInstalling = false;
            IsBusy = false;
            NotifyDownloadActions();
        }
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        _installCancellation?.Cancel();
        Notify(nameof(CanCancelInstall));
        InstallDetail = "正在取消，已完成的文件会保留…";
    }

    public void LoadSmokeCatalog(string path)
    {
        _allRemoteVersions = MinecraftDownloadService.ParseManifest(File.ReadAllBytes(path));
        FilterRemoteVersions();
        _catalogLoaded = true;
    }

    public void LoadSmokeVersionDetails(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var doc = System.Text.Json.JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var version = _allRemoteVersions.Single(item => item.Id == root.GetProperty("id").GetString());
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)), version.MetadataSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Preview metadata checksum mismatch.");
        VersionSearch = version.Id;
        SelectedRemoteVersion = version;
        _selectedInstallInfo = MinecraftDownloadService.ParseVersionInfo(version, bytes);
        RemoteJava = $"Java {_selectedInstallInfo.RequiredJava} · 64 位";
        RemoteSize = FormatBytes(_selectedInstallInfo.EstimatedBytes);
        NotifyDownloadActions();
    }

    public void OpenDownloads() => DownloadsNav.IsChecked = true;

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        foreach (var unit in new[] { "B", "KB", "MB", "GB", "TB" })
        {
            if (value < 1024 || unit == "TB") return $"{value:0.#} {unit}";
            value /= 1024;
        }
        return "—";
    }
}
