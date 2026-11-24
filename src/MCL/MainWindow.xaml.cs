using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsStore _store;
    private readonly LogService _log;
    private readonly VersionCatalog _catalog = new();
    private readonly JavaDiscovery _javaDiscovery = new();
    private readonly GameLaunchService _launcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _initialVersionId;
    private string _gameDirectory;
    private string _javaPath;
    private string _playerName;
    private int _memoryMb;
    private GameVersion? _selectedVersion;
    private JavaInstallation? _java;
    private bool _isBusy;
    private bool _isGameRunning;
    private bool _isDirty;
    private bool _loading = true;
    private bool _closing;
    private string _pageTitle = "欢迎回来";
    private string _statusMessage = "准备就绪。选择已有的 Minecraft 文件夹，开始你的第一次出发。";
    private string _currentPage = "Home";

    public ObservableCollection<GameVersion> Versions { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow(string? dataDirectory = null, bool offlinePreview = false, IVersionRecycleBin? recycleBin = null,
        IMicrosoftAccountService? accountService = null, IAccountStore? accountStore = null)
    {
        _offlinePreview = offlinePreview;
        _store = new SettingsStore(dataDirectory);
        _log = new LogService(_store.DataDirectory);
        _log.EntryAdded += OnLogEntry;
        _accountService = accountService ?? new MicrosoftAccountService();
        _accountStore = accountStore ?? new AccountStore(_store.DataDirectory);
        _launcher = new GameLaunchService(_log);
        _launcher.RunningChanged += OnRunningChanged;
        _launcher.LanPortChanged += OnLanPortChanged;
        _versionManager = new VersionManagementService(recycleBin, () => _launcher.IsRunning);
        _modpacks = new ModpackImportService(_downloads);
        var settings = _store.Load();
        _gameDirectory = GameDirectoryResolver.Normalize(settings.GameDirectory);
        _javaPath = settings.JavaPath;
        _playerName = settings.PlayerName;
        _memoryMb = Math.Clamp(settings.MemoryMb, 1024, 16384);
        _initialVersionId = settings.SelectedVersionId ?? "";
        _microsoftClientId = settings.MicrosoftClientId ?? "";
        _useMicrosoftAccount = settings.UseMicrosoftAccount;
        _relayEndpoint = string.IsNullOrWhiteSpace(settings.RelayEndpoint) ? DefaultRelayEndpoint() : settings.RelayEndpoint;
        LoadSavedAccount();
        InitializeComponent();
        DataContext = this;
        HomeNav.IsChecked = true;
        _loading = false;
        _log.Info("MCL 0.6 已打开。");
    }

    public string GameDirectory
    {
        get => _gameDirectory;
        set
        {
            if (!Set(ref _gameDirectory, value)) return;
            MarkDirty();
            // Prevent launching a stale selection from a different directory.
            if (!_loading) { Versions.Clear(); SelectedVersion = null; UpdateVersionState(); }
        }
    }

    public string JavaPath
    {
        get => _javaPath;
        set
        {
            if (!Set(ref _javaPath, value)) return;
            _java = null;
            MarkDirty();
            Notify(nameof(JavaSummary), nameof(JavaDetail));
        }
    }

    public string PlayerName { get => _playerName; set { if (Set(ref _playerName, value)) { MarkDirty(); NotifyAccountState(); } } }
    public int MemoryMb { get => _memoryMb; set { if (Set(ref _memoryMb, value)) { MarkDirty(); Notify(nameof(MemorySummary)); } } }
    public string MemorySummary => $"{MemoryMb:N0} MB";
    public GameVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!Set(ref _selectedVersion, value)) return;
            MarkDirty();
            Notify(nameof(SelectedVersionTitle), nameof(SelectedVersionDescription), nameof(CanLaunch));
            NotifyVersionManagement();
            NotifyMultiplayer();
        }
    }

    public string SelectedVersionTitle => SelectedVersion?.DisplayName ?? "选择你的第一个版本";
    public string SelectedVersionDescription => SelectedVersion is null
        ? "添加已有游戏后，在版本列表里选择一个原版版本。"
        : SelectedVersion.IsValid ? $"{SelectedVersion.Kind} · {ActiveAccountLabel} {ActivePlayerName}" : SelectedVersion.Issue ?? "版本文件需要检查";
    public string JavaSummary => _java is null ? (IsBusy ? "检测中…" : "待配置") : $"Java {_java.MajorVersion} · {(_java.Is64Bit ? "64 位" : "32 位")}";
    public string JavaDetail => _java is null ? "自动检测本机 Java，或手动选择 java.exe。启动前会再次检查。" : $"已检测：Java {_java.VersionText} · {(_java.Is64Bit ? "64 位" : "32 位")}";
    public string VersionCountLabel => $"{Versions.Count} 个";
    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string SettingsStatus => _isDirty ? "有尚未保存的修改" : "当前没有未保存的修改";
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { Notify(nameof(CanInteract), nameof(CanLaunch), nameof(CanStop), nameof(LaunchButtonText), nameof(ActivityText), nameof(JavaSummary)); NotifyDownloadActions(); NotifyVersionManagement(); NotifyAccountState(); NotifyPackActions(); NotifyMultiplayer(); } } }
    public bool CanInteract => !IsBusy;
    public bool CanLaunch => !IsBusy && !IsGameRunning && SelectedVersion is { IsValid: true }
        && (!UseMicrosoftAccount || (HasMicrosoftAccount && !_accountRequiresLogin));
    public bool CanStop => !IsBusy && IsGameRunning;
    public string LaunchButtonText => IsBusy ? "正在准备…" : IsGameRunning ? "游戏运行中" : UseMicrosoftAccount
        ? !HasMicrosoftAccount || _accountRequiresLogin ? "请先登录微软账号" : "▶  使用微软账号启动" : "▶  启动本地游戏";
    public bool IsGameRunning { get => _isGameRunning; private set { if (Set(ref _isGameRunning, value)) { Notify(nameof(CanLaunch), nameof(CanStop), nameof(LaunchButtonText), nameof(ActivityText)); NotifyVersionManagement(); NotifyAccountState(); NotifyPackActions(); NotifyMultiplayer(); } } }
    public string ActivityText => PackInProgress ? "正在安装整合包" : IsInstalling ? "正在安装游戏" : _isAuthenticating ? "正在验证账号" : IsBusy ? "正在处理" : IsGameRunning ? "游戏运行中" : "游戏工作空间";

    public async Task InitializeAsync()
    {
        _loading = true;
        try
        {
            await RunBusyAsync(async () =>
            {
                await ScanVersionsAsync(_initialVersionId);
                if (!string.IsNullOrWhiteSpace(JavaPath))
                {
                    _java = await _javaDiscovery.InspectAsync(JavaPath, _lifetime.Token);
                    if (_java is null) _log.Warn("保存的 Java 路径不可用，可以在启动设置中重新检测。");
                    Notify(nameof(JavaSummary), nameof(JavaDetail));
                }
                else await DetectJavaAsync();
                StatusMessage = Versions.Count == 0 ? "欢迎使用 MCL。可以下载一个新版本，或选择已有的游戏文件夹。" : $"发现 {Versions.Count} 个本地版本。选择完整原版后即可尝试启动。";
            });
        }
        finally { _loading = false; }
        if (!_offlinePreview && HasMicrosoftAccount && !_closing) _ = LoadAccountAvatarAsync();
        if (_currentPage == "Downloads" && !_catalogLoaded && !_offlinePreview && !_closing) await LoadRemoteVersionsAsync();
    }

    private async Task ScanVersionsAsync(string? preferredId = null)
    {
        var directory = GameDirectory.Trim();
        var desiredId = preferredId ?? SelectedVersion?.Id;
        var versions = await _catalog.ScanAsync(directory, _lifetime.Token);
        var wasLoading = _loading;
        _loading = true;
        try
        {
            Versions.Clear();
            foreach (var version in versions) Versions.Add(version);
            SelectedVersion = Versions.FirstOrDefault(version => version.Id == desiredId)
                ?? Versions.FirstOrDefault(version => version.IsValid && version.Kind == "原版")
                ?? Versions.FirstOrDefault();
        }
        finally { _loading = wasLoading; }
        if (!wasLoading && desiredId != SelectedVersion?.Id) MarkDirty();
        UpdateVersionState();
        _log.Info($"扫描完成，发现 {Versions.Count} 个本地版本。");
    }

    private void UpdateVersionState()
    {
        Notify(nameof(VersionCountLabel));
        if (EmptyVersions is not null) EmptyVersions.Visibility = Versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task DetectJavaAsync()
    {
        StatusMessage = "正在查找本机 Java…";
        var installations = await _javaDiscovery.FindAsync(_lifetime.Token);
        var chosen = installations.FirstOrDefault(java => java.Is64Bit) ?? installations.FirstOrDefault();
        if (chosen is not null)
        {
            JavaPath = chosen.Path;
            _java = chosen;
            _log.Info($"已检测到 Java {chosen.MajorVersion}（{(chosen.Is64Bit ? "64" : "32")} 位）。");
            StatusMessage = $"已选择 Java {chosen.MajorVersion}。修改后可在启动设置中保存。";
        }
        else
        {
            _java = null;
            _log.Warn("没有检测到可用 Java，请在启动设置中手动选择。");
            StatusMessage = "没有找到可用 Java，请在启动设置中选择 java.exe。";
        }
        Notify(nameof(JavaSummary), nameof(JavaDetail));
    }

    private async void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page } && HomePage is not null)
        {
            Navigate(page);
            if (page == "Downloads" && !_catalogLoaded) await LoadRemoteVersionsAsync();
        }
    }

    private void Navigate(string page)
    {
        _currentPage = page;
        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        VersionsPage.Visibility = page == "Versions" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPage.Visibility = page == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
        ModpacksPage.Visibility = page == "Modpacks" ? Visibility.Visible : Visibility.Collapsed;
        MultiplayerPage.Visibility = page == "Multiplayer" ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = page == "Accounts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        FooterSaveButton.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        FooterSlogan.Visibility = page == "Settings" ? Visibility.Collapsed : Visibility.Visible;
        PageTitle = page switch { "Versions" => "你的游戏版本", "Downloads" => "下载一个新世界", "Modpacks" => "导入整合包", "Multiplayer" => "好友联机", "Accounts" => "你的游戏账号", "Settings" => "启动设置", "Logs" => "运行日志", _ => "欢迎回来" };
        if (page == "Logs" && Logs.Count > 0) LogList.ScrollIntoView(Logs[^1]);
    }

    private void ShowVersions_Click(object sender, RoutedEventArgs e) => VersionsNav.IsChecked = true;
    private void ShowSettings_Click(object sender, RoutedEventArgs e) => SettingsNav.IsChecked = true;

    private async void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new OpenFolderDialog { Title = "选择 Minecraft 游戏文件夹（其中应包含 versions 文件夹）", Multiselect = false };
        if (Directory.Exists(GameDirectory)) dialog.InitialDirectory = GameDirectory;
        if (dialog.ShowDialog(this) != true) return;
        var selected = GameDirectoryResolver.Normalize(dialog.FolderName);
        GameDirectory = selected;
        await RunBusyAsync(async () =>
        {
            await ScanVersionsAsync();
            SaveCurrentSettings();
            StatusMessage = Versions.Count == 0 ? "这个文件夹中没有发现版本，请确认选择的是包含 versions 的游戏目录。" : $"游戏目录已保存，发现 {Versions.Count} 个版本。";
            VersionsNav.IsChecked = true;
        });
    }

    private async void RefreshVersions_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync(async () => { await ScanVersionsAsync(); StatusMessage = $"已刷新版本列表，共 {Versions.Count} 个版本。"; });

    private async void DetectJava_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync(DetectJavaAsync);

    private async void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog { Title = "选择 Java 程序", Filter = "Java 程序 (java.exe;javaw.exe)|java.exe;javaw.exe|可执行程序 (*.exe)|*.exe", CheckFileExists = true };
        if (File.Exists(JavaPath)) dialog.InitialDirectory = Path.GetDirectoryName(JavaPath);
        if (dialog.ShowDialog(this) != true) return;
        JavaPath = dialog.FileName;
        await RunBusyAsync(async () =>
        {
            var installation = await _javaDiscovery.InspectAsync(JavaPath, _lifetime.Token);
            if (installation is null) throw new InvalidDataException("这个程序未能返回有效的 Java 版本，请选择可用的 java.exe。");
            JavaPath = installation.Path;
            _java = installation;
            Notify(nameof(JavaSummary), nameof(JavaDetail));
            StatusMessage = "Java 检查通过，请保存设置。";
        });
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync(async () =>
        {
            SaveCurrentSettings();
            await ScanVersionsAsync();
            if (!string.IsNullOrWhiteSpace(JavaPath)) _java = await _javaDiscovery.InspectAsync(JavaPath, _lifetime.Token);
            Notify(nameof(JavaSummary), nameof(JavaDetail));
            // Scanning may choose a default version; persist that choice too.
            SaveCurrentSettings();
            StatusMessage = "设置已保存。下次打开 MCL 时会自动恢复。";
        });

    private LauncherSettings SnapshotSettings()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory)) throw new InvalidDataException("请先填写或选择游戏文件夹。");
        if (!UseMicrosoftAccount && !Regex.IsMatch(PlayerName ?? "", "\\A[A-Za-z0-9_]{1,16}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("玩家名称需要使用 1–16 位英文字母、数字或下划线。");
        if (MemoryMb is < 1024 or > 16384) throw new InvalidDataException("请把最大游戏内存设置在 1024–16384 MB 之间。");
        return new LauncherSettings
        {
            GameDirectory = GameDirectoryResolver.Normalize(GameDirectory),
            JavaPath = JavaPath.Trim().Trim('"'),
            PlayerName = PlayerName!,
            MemoryMb = MemoryMb,
            SelectedVersionId = SelectedVersion?.Id,
            MicrosoftClientId = MicrosoftClientId?.Trim() ?? "",
            UseMicrosoftAccount = UseMicrosoftAccount,
            RelayEndpoint = RelayEndpoint.Trim()
        };
    }

    private void SaveCurrentSettings()
    {
        _store.Save(SnapshotSettings());
        _isDirty = false;
        Notify(nameof(SettingsStatus));
        _log.Info("启动设置已保存。");
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLaunch || SelectedVersion is not { } version) return;
        await RunBusyAsync(() => LaunchGameAsync(version, null, _lifetime.Token));
    }

    private async Task LaunchGameAsync(GameVersion version, int? quickJoinPort, CancellationToken ct)
    {
        var settings = SnapshotSettings();
        StatusMessage = "正在检查 Java 和本地游戏文件…";
        _java = await _javaDiscovery.InspectAsync(settings.JavaPath, ct);
        Notify(nameof(JavaSummary), nameof(JavaDetail));
        if (_java is null) throw new InvalidDataException("Java 未配置或不可用。请到启动设置中点击自动检测，或选择 java.exe。");
        SaveCurrentSettings();
        var identity = await PrepareAccountForLaunchAsync();
        ct.ThrowIfCancellationRequested();
        _runningGame = version;
        try { await _launcher.LaunchAsync(settings, version, _java, ct, identity, quickJoinPort); }
        catch { _runningGame = null; throw; }
        StatusMessage = "启动命令已执行。游戏输出会显示在运行日志中。";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStop) return;
        if (MessageBox.Show(this, "强制停止可能丢失尚未保存的游戏进度。建议先在游戏中保存并退出。\n\n仍要停止当前游戏吗？", "停止游戏", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _launcher.Stop(); StatusMessage = "已请求停止当前游戏。"; }
        catch (Exception ex) { ReportError("停止失败", ex); }
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedVersion is not null && !IsBusy) StatusMessage = $"已选择 {SelectedVersion.DisplayName}。可回到首页启动。";
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, Logs.Select(entry => entry.ToString()))); StatusMessage = "日志已复制。"; }
        catch (Exception ex) { ReportError("复制失败", ex); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(_log.LogFilePath)!;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { ReportError("无法打开日志文件夹", ex); }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy || _closing) return;
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { if (!_closing) StatusMessage = "操作已取消。"; }
        catch (Exception ex) { if (!_closing) ReportError("操作未能完成", ex); }
        finally { IsBusy = false; }
    }

    public void ReportError(string title, Exception ex)
    {
        _log.Error($"{title}：{ex}");
        StatusMessage = ex.Message;
        if (IsVisible && !_offlinePreview) MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnLogEntry(LogEntry entry)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnLogEntry(entry)); return; }
        Logs.Add(entry);
        while (Logs.Count > 600) Logs.RemoveAt(0);
        if (_currentPage == "Logs") LogList.ScrollIntoView(entry);
    }

    private void OnRunningChanged(bool running)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnRunningChanged(running)); return; }
        IsGameRunning = running;
        if (!running)
        {
            _runningGame = null;
            _lanPort = null;
            if (_hostingRoom || _guestLaunched) EndMultiplayer("游戏已退出，联机已结束。");
            NotifyMultiplayer();
            StatusMessage = "游戏进程已结束，详细信息可在运行日志中查看。";
        }
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _isDirty = true;
        Notify(nameof(SettingsStatus), nameof(SelectedVersionDescription));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isDeletingVersion) { e.Cancel = true; StatusMessage = "正在将版本移入回收站，请稍候再关闭。"; return; }
        if (PackInProgress) { e.Cancel = true; OpenModpacks(); StatusMessage = "正在安装整合包，请完成安装或取消后再关闭。"; return; }
        if (IsInstalling && IsVisible && MessageBox.Show(this, "关闭 MCL 会取消当前下载。已下载并校验完成的文件会保留。\n\n现在关闭吗？", "正在下载游戏", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        if (_isDirty && IsVisible)
        {
            var choice = MessageBox.Show(this, "要保存当前启动设置吗？", "关闭 MCL", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (choice == MessageBoxResult.Yes)
            {
                try { SaveCurrentSettings(); }
                catch (Exception ex) { ReportError("设置未保存", ex); e.Cancel = true; return; }
            }
        }
        _closing = true;
        _accountCancellation?.Cancel();
        _avatarCancellation?.Cancel();
        _installCancellation?.Cancel();
        _previewCancellation?.Cancel();
        _multiplayerCancellation?.Cancel();
        _lifetime.Cancel();
        _log.EntryAdded -= OnLogEntry;
        _launcher.RunningChanged -= OnRunningChanged;
        _launcher.LanPortChanged -= OnLanPortChanged;
        _launcher.Dispose();
        _modpacks.Dispose();
        _downloads.Dispose();
        _avatars.Dispose();
        if (_accountService is IDisposable disposableAccountService) disposableAccountService.Dispose();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var darkMode = 0;
            var captionColor = 0x00FBF8FF; // Windows COLORREF: #FFF8FB.
            var captionTextColor = 0x00392D43; // #432D39.
            DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
            DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref captionTextColor, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void Notify(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public async Task ValidateUiAsync()
    {
        if (IsBusy) throw new InvalidOperationException("UI initialization is still busy.");
        if (_isDirty) throw new InvalidOperationException("Loading existing settings must not mark them as edited.");
        IsBusy = true;
        SettingsPage.UpdateLayout();
        if (SettingsPage.IsEnabled || VersionList.IsEnabled || RemoteVersionList.IsEnabled || CanStop || CanInstall) throw new InvalidOperationException("Input must be disabled while a task is active.");
        IsBusy = false;
        foreach (var page in new[] { "Versions", "Downloads", "Modpacks", "Multiplayer", "Accounts", "Settings", "Logs", "Home" })
        {
            SelectSmokePage(page);
            var visible = new[] { HomePage, VersionsPage, DownloadsPage, ModpacksPage, MultiplayerPage, AccountsPage, SettingsPage, LogsPage }.Count(panel => panel.Visibility == Visibility.Visible);
            if (visible != 1) throw new InvalidOperationException("Navigation did not select exactly one page.");
        }
        var oldPlayer = PlayerName;
        PlayerName = "MCL_Smoke";
        SaveCurrentSettings();
        if (_store.Load().PlayerName != "MCL_Smoke") throw new InvalidOperationException("Settings did not persist.");
        PlayerName = oldPlayer;
        SaveCurrentSettings();
        await ScanVersionsAsync();
        _isDirty = false;
        Notify(nameof(SettingsStatus));
        HomeNav.IsChecked = true;
        Navigate("Home");
    }

    public void WriteSmokeResult(string text)
    {
        Directory.CreateDirectory(_store.DataDirectory);
        File.WriteAllText(Path.Combine(_store.DataDirectory, "ui-smoke-result.txt"), text);
    }

    public void SelectSmokePage(string page)
    {
        var button = page switch { "Versions" => VersionsNav, "Downloads" => DownloadsNav, "Modpacks" => ModpacksNav, "Multiplayer" => MultiplayerNav, "Accounts" => AccountsNav, "Settings" => SettingsNav, "Logs" => LogsNav, _ => HomeNav };
        button.IsChecked = true;
        Navigate(page);
    }
}
