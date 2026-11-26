using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsStore _store;
    private readonly LogService _log;
    private readonly VersionCatalog _catalog = new();
    private readonly GameLaunchService _launcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _initialVersionId;
    private string _gameDirectory;
    private string _playerName;
    private int _memoryMb;
    private GameVersion? _selectedVersion;
    private bool _isBusy;
    private bool _isGameRunning;
    private bool _isDirty;
    private bool _loading = true;
    private bool _closing;
    private string _pageTitle = "首页";
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
        _profileService = accountService ?? new MicrosoftAccountService();
        _profileStore = accountStore ?? new AccountStore(_store.DataDirectory);
        _launcher = new GameLaunchService(_log);
        _launcher.RunningChanged += OnRunningChanged;
        _launcher.LanPortChanged += OnLanPortChanged;
        _versionManager = new VersionManagementService(recycleBin, () => _launcher.IsRunning);
        _modpacks = new ModpackImportService(_downloads);
        _localAppearance = new LocalAppearanceService(_store.DataDirectory, _modpacks);
        var settings = _store.Load();
        _gameDirectory = GameDirectoryResolver.Normalize(settings.GameDirectory);
        _javaPath = settings.JavaPath;
        _autoMatchJava = settings.AutoMatchJava;
        _playerName = settings.PlayerName;
        _memoryMb = Math.Clamp(settings.MemoryMb, 1024, 16384);
        _initialVersionId = settings.SelectedVersionId ?? "";
        _relayEndpoint = string.IsNullOrWhiteSpace(settings.RelayEndpoint) ? DefaultRelayEndpoint() : settings.RelayEndpoint;
        LoadLocalAppearance(settings);
        LoadOnlineProfile(settings.MicrosoftClientId);
        InitializeComponent();
        UpdateCharacterPreview();
        DataContext = this;
        HomeNav.IsChecked = true;
        _loading = false;
        _log.Info($"MCL {AppVersionText} 已打开。");
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

    public string PlayerName { get => _playerName; set { if (Set(ref _playerName, value)) { MarkDirty(); Notify(nameof(SelectedVersionDescription)); } } }
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
            NotifyJava();
            NotifyMultiplayer();
            AnimateVersionArtwork();
        }
    }

    public string SelectedVersionTitle => SelectedVersion?.DisplayName ?? "选择你的第一个版本";
    public string SelectedVersionDescription => SelectedVersion is null
        ? "下载新版本，或选择已有游戏文件夹。"
        : SelectedVersion.IsValid ? $"{SelectedVersion.Kind} · 本地档案 {PlayerName}" : SelectedVersion.Issue ?? "版本文件需要检查";
    public string VersionCountLabel => $"{Versions.Count} 个";
    public string AppVersionText => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string SettingsStatus => _isDirty ? "有尚未保存的修改" : "当前没有未保存的修改";
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { Notify(nameof(CanInteract), nameof(CanLaunch), nameof(CanStop), nameof(LaunchButtonText), nameof(ActivityText), nameof(JavaSummary), nameof(CanEditLocalAppearance)); NotifyDownloadActions(); NotifyVersionManagement(); NotifyPackActions(); NotifyMultiplayer(); NotifyOnlineProfile(); } } }
    public bool CanInteract => !IsBusy;
    public bool CanLaunch => !IsBusy && !IsGameRunning && SelectedVersion is { IsValid: true };
    public bool CanStop => !IsBusy && IsGameRunning;
    public string LaunchButtonText => IsBusy ? "正在准备…" : IsGameRunning ? "游戏运行中" : "▶  启动本地游戏";
    public bool IsGameRunning { get => _isGameRunning; private set { if (Set(ref _isGameRunning, value)) { Notify(nameof(CanLaunch), nameof(CanStop), nameof(LaunchButtonText), nameof(ActivityText), nameof(CanEditLocalAppearance)); NotifyVersionManagement(); NotifyPackActions(); NotifyMultiplayer(); NotifyOnlineProfile(); } } }
    public string ActivityText => PackInProgress ? "正在安装整合包" : IsInstalling ? "正在安装游戏" : IsBusy ? "正在处理" : IsGameRunning ? "游戏运行中" : "就绪";

    public async Task InitializeAsync()
    {
        _loading = true;
        try
        {
            await RunBusyAsync(async () =>
            {
                await ScanVersionsAsync(_initialVersionId);
                await ScanJavaAsync(_lifetime.Token);
                StatusMessage = Versions.Count == 0 ? "欢迎使用 MCL。可以下载一个新版本，或选择已有的游戏文件夹。" : $"发现 {Versions.Count} 个本地版本。选择完整原版后即可尝试启动。";
            });
        }
        finally { _loading = false; }
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
        if (VersionsWorkspace is not null) VersionsWorkspace.Visibility = Versions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RevealSelectedVersion();
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
        var changed = _currentPage != page;
        _currentPage = page;
        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        VersionsPage.Visibility = page == "Versions" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPage.Visibility = page == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
        ModpacksPage.Visibility = page == "Modpacks" ? Visibility.Visible : Visibility.Collapsed;
        MultiplayerPage.Visibility = page == "Multiplayer" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = page == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        FooterSaveButton.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle = page switch { "Versions" => "游戏版本", "Downloads" => "下载游戏", "Modpacks" => "导入整合包", "Multiplayer" => "好友联机", "Appearance" => "本地角色", "Settings" => "启动设置", "Logs" => "运行日志", _ => "首页" };
        if (changed)
        {
            PageScroll.ScrollToTop();
            AnimatePage();
            RevealSelectedVersion();
        }
        if (page == "Logs" && Logs.Count > 0) LogList.ScrollIntoView(Logs[^1]);
    }

    private void AnimatePage()
    {
        if (!IsLoaded || _offlinePreview || !SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(210);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.58, 1, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        });
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        });
    }

    private void ShowVersions_Click(object sender, RoutedEventArgs e) => VersionsNav.IsChecked = true;
    private void ShowModpacks_Click(object sender, RoutedEventArgs e) => OpenModpacks();
    private void ShowMultiplayer_Click(object sender, RoutedEventArgs e) => OpenMultiplayer();
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

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync(async () =>
        {
            SaveCurrentSettings();
            await ScanVersionsAsync();
            await ScanJavaAsync(_lifetime.Token);
            // Scanning may choose a default version; persist that choice too.
            SaveCurrentSettings();
            StatusMessage = "设置已保存。下次打开 MCL 时会自动恢复。";
        });

    private LauncherSettings SnapshotSettings()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory)) throw new InvalidDataException("请先填写或选择游戏文件夹。");
        if (!MinecraftMetadata.IsValidLocalPlayerName(PlayerName)) throw new InvalidDataException("玩家名称需要使用 1–16 位英文字母、数字或下划线。");
        if (MemoryMb is < 1024 or > 16384) throw new InvalidDataException("请把最大游戏内存设置在 1024–16384 MB 之间。");
        return new LauncherSettings
        {
            GameDirectory = GameDirectoryResolver.Normalize(GameDirectory),
            JavaPath = JavaPath.Trim().Trim('"'),
            AutoMatchJava = AutoMatchJava,
            PlayerName = PlayerName!,
            MemoryMb = MemoryMb,
            SelectedVersionId = SelectedVersion?.Id,
            MicrosoftClientId = MicrosoftClientId.Trim(),
            RelayEndpoint = RelayEndpoint.Trim(),
            ApplyLocalAppearance = ApplyLocalAppearance,
            LocalSkin = _localSkin,
            LocalCape = _localCape,
            LocalSkinModel = UseSlimSkin ? "slim" : "default"
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
        await RunBusyAsync(() => LaunchGameAsync(version, _lifetime.Token));
    }

    private async Task LaunchGameAsync(GameVersion version, CancellationToken ct, bool joiningFriend = false)
    {
        var settings = SnapshotSettings();
        StatusMessage = "正在检查 Java 和本地游戏文件…";
        var java = await ResolveJavaForLaunchAsync(version, ct);
        _log.Info($"版本 {version.Id} 使用 {DescribeJava(java)}（{java.Path}）。");
        SaveCurrentSettings();
        LocalAppearanceLaunch? appearance = null;
        if (settings.ApplyLocalAppearance && (settings.LocalSkin is not null || settings.LocalCape is not null) && version.Kind != "Forge 整合包")
        {
            if (LocalAppearanceService.Supports(version))
            {
                StatusMessage = "正在准备本地皮肤和披风，首次使用需要下载外观组件…";
                appearance = await Task.Run(() => _localAppearance.PrepareAsync(settings, version, ct), ct);
            }
            else _log.Info($"版本 {version.Id} 暂不兼容本地皮肤和披风，使用游戏默认外观启动。");
        }
        ct.ThrowIfCancellationRequested();
        _runningGame = version;
        _runningForGuest = joiningFriend;
        try
        {
            if (appearance is null) await _launcher.LaunchAsync(settings, version, java, ct, joiningFriend);
            else await _launcher.LaunchWithAppearanceAsync(settings, version, java, appearance, ct, joiningFriend);
        }
        catch { _runningGame = null; _runningForGuest = false; throw; }
        StatusMessage = "启动命令已执行。游戏输出会显示在运行日志中。";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStop) return;
        if (MessageBox.Show(this, "强制停止可能丢失尚未保存的游戏进度。建议先在游戏中保存并退出。\n\n仍要停止当前游戏吗？", "停止游戏", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _launcher.Stop(); StatusMessage = "已请求停止当前游戏。"; }
        catch (Exception ex) { ReportError("停止失败", ex); }
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
            _runningForGuest = false;
            _lanPort = null;
            if (_hostingRoom || _guestUsesGame) EndMultiplayer("游戏已退出，联机已结束。");
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
        if (_profileService is IDisposable profileService) profileService.Dispose();
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
        if (SettingsPage.IsEnabled || HomeVersionPicker.IsEnabled || VersionList.IsEnabled || RemoteVersionList.IsEnabled || LaunchSelectedVersionButton.IsEnabled
            || CanStop || CanInstall) throw new InvalidOperationException("Input must be disabled while a task is active.");
        IsBusy = false;
        foreach (var page in new[] { "Versions", "Downloads", "Modpacks", "Multiplayer", "Appearance", "Settings", "Logs", "Home" })
        {
            SelectSmokePage(page);
            var visible = new[] { HomePage, VersionsPage, DownloadsPage, ModpacksPage, MultiplayerPage, AppearancePage, SettingsPage, LogsPage }.Count(panel => panel.Visibility == Visibility.Visible);
            if (visible != 1) throw new InvalidOperationException("Navigation did not select exactly one page.");
        }
        if (HomeVersionPicker.Items.Count != Versions.Count || HomeVersionPicker.IsEnabled != (Versions.Count > 0))
            throw new InvalidOperationException("Home version picker must show the installed versions.");
        if (SelectedVersion is { } homeSelection && Versions.FirstOrDefault(version => version != homeSelection) is { } homeAlternative)
        {
            HomeVersionPicker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, homeAlternative);
            if (SelectedVersion != homeAlternative || !Equals(VersionList.SelectedItem, homeAlternative) || CanLaunch != homeAlternative.IsValid)
                throw new InvalidOperationException("Choosing a version on Home must update the selected version and launch action.");
            HomeVersionPicker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, homeSelection);
        }
        SelectSmokePage("Versions");
        if ((Versions.Count == 0) != (EmptyVersions.Visibility == Visibility.Visible)
            || (Versions.Count > 0) != (VersionsWorkspace.Visibility == Visibility.Visible))
            throw new InvalidOperationException("The list and empty version views must be mutually exclusive.");
        if (SelectedVersion is { } selected && Versions.FirstOrDefault(version => version != selected) is { } other)
        {
            VersionList.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, other);
            if (SelectedVersion != other || ManagedMinecraftVersion != (other.ModpackMinecraft ?? other.Id)
                || VersionManagementCard.Visibility != Visibility.Visible || LaunchSelectedVersionButton.IsEnabled != other.IsValid)
                throw new InvalidOperationException("Version details and actions must follow the current selection.");
            VersionList.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, selected);
        }
        SelectSmokePage("Multiplayer");
        GuestRoleButton.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        if (GuestRoleButton.IsChecked != true || HostRoleButton.IsChecked != false
            || GuestRoomCard.Visibility != Visibility.Visible || HostRoomCard.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Joining must show only the guest setup.");
        InviteInput = "smoke-invite";
        if (!JoinRoomButton.IsEnabled || !Equals(JoinRoomButton.Content, "加入并启动"))
            throw new InvalidOperationException("Joining with no game running must offer to start it.");
        var runningGame = new GameVersion("1.21.11", "", "", "原版", null, true, null);
        _runningGame = runningGame;
        IsGameRunning = true;
        if (!JoinRoomButton.IsEnabled || !Equals(JoinRoomButton.Content, "加入房间")
            || !ReferenceEquals(await ResolveJoiningVersionAsync(Describe(runningGame), runningGame, _lifetime.Token), runningGame))
            throw new InvalidOperationException("Joining with a matching game must reuse the running instance.");
        var rejectedMismatch = false;
        try { await ResolveJoiningVersionAsync(Describe(runningGame) with { Pack = "Smoke Pack", PackVersion = "1" }, runningGame, _lifetime.Token); }
        catch (InvalidDataException) { rejectedMismatch = true; }
        if (!rejectedMismatch) throw new InvalidOperationException("Joining must reject a mismatched running game.");
        _runningGame = null;
        IsGameRunning = false;
        InviteInput = "";
        HostRoleButton.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        if (HostRoleButton.IsChecked != true || GuestRoleButton.IsChecked != false
            || HostRoomCard.Visibility != Visibility.Visible || GuestRoomCard.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Hosting must show only the host setup.");
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
        var button = page switch { "Versions" => VersionsNav, "Downloads" => DownloadsNav, "Modpacks" => ModpacksNav, "Multiplayer" => MultiplayerNav, "Appearance" => AppearanceNav, "Settings" => SettingsNav, "Logs" => LogsNav, _ => HomeNav };
        button.IsChecked = true;
        Navigate(page);
    }
}
