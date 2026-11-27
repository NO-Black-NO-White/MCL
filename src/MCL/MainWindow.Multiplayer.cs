using System.Net.Http;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MCL.Models;
using MCL.Multiplayer;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private GameVersion? _runningGame;
    private int? _lanPort;
    private CancellationTokenSource? _multiplayerCancellation;
    private bool _hostingRoom;
    private bool _guestUsesGame;
    private bool _runningForGuest;
    private bool _showHostSetup = true;
    private bool _useSharedRelay;
    private bool _useIpv6Direct;
    private TaskCompletionSource<int>? _lanReady;
    private string _relayEndpoint = "";
    private string _inviteInput = "";
    private string _roomCode = "";
    private string _multiplayerStatus = "";
    private string _multiplayerStage = "";
    private bool _retryHost;
    private bool _retryJoin;
    private bool _rejoinRequested;
    private RoomDiagnostic? _roomDiagnostic;

    public string RelayEndpoint
    {
        get => _relayEndpoint;
        set { if (Set(ref _relayEndpoint, value)) { NotifySettingsChanged(); NotifyMultiplayer(); } }
    }

    public string InviteInput
    {
        get => _inviteInput;
        set
        {
            if (!Set(ref _inviteInput, value)) return;
            _retryJoin = false;
            Notify(nameof(CanJoinRoom), nameof(JoinRoomButtonText));
        }
    }

    public string RoomCode { get => _roomCode; private set { if (Set(ref _roomCode, value)) Notify(nameof(HasRoomCode)); } }
    public bool HasRoomCode => RoomCode.Length != 0;
    public bool ShowHostSetup
    {
        get => _showHostSetup;
        set
        {
            if (!value || !CanEditRelay || !Set(ref _showHostSetup, true)) return;
            Notify(nameof(ShowGuestSetup));
            MultiplayerStatus = "";
            MultiplayerStage = "";
        }
    }
    public bool ShowGuestSetup
    {
        get => !ShowHostSetup;
        set
        {
            if (!value || !CanEditRelay || !Set(ref _showHostSetup, false, nameof(ShowHostSetup))) return;
            Notify(nameof(ShowGuestSetup));
            MultiplayerStatus = "";
            MultiplayerStage = "";
        }
    }
    public bool UseSharedRelay
    {
        get => _useSharedRelay;
        set { if (Set(ref _useSharedRelay, value)) NotifyMultiplayer(); }
    }
    public bool UseIpv6Direct
    {
        get => _useIpv6Direct;
        set
        {
            if (!Set(ref _useIpv6Direct, value)) return;
            if (value) Set(ref _useSharedRelay, false, nameof(UseSharedRelay));
            NotifyMultiplayer();
        }
    }
    public bool CanChooseSharedRelay => CanEditRelay && !UseIpv6Direct;
    public string HostTransportHint => UseIpv6Direct
        ? "IPv6 直连需要双方有 IPv6，房主网络允许入站连接。"
        : UseSharedRelay ? "使用下方「手动中转设置」中的地址。"
        : "进入存档后选择「对局域网开放」，MCL 会准备公网隧道。";
    public string MultiplayerStatus { get => _multiplayerStatus; private set => Set(ref _multiplayerStatus, value); }
    public string MultiplayerStage
    {
        get => _multiplayerStage;
        private set
        {
            Set(ref _multiplayerStage, value);
            if (_roomDiagnostic?.Record(value) == true) Notify(nameof(RoomDiagnosticDetails));
        }
    }
    public bool HasRoomDiagnostic => _roomDiagnostic is not null;
    public string RoomDiagnosticRoute => _roomDiagnostic is { } diagnostic ? $"连接方式 · {diagnostic.RouteName}" : "";
    public string RoomDiagnosticDetails => _roomDiagnostic?.Details ?? "";

    private void StartRoomDiagnostic(bool hosting, RoomRoute route)
    {
        ClearLatency();
        _roomDiagnostic = new RoomDiagnostic(hosting, route);
        Notify(nameof(HasRoomDiagnostic), nameof(RoomDiagnosticRoute), nameof(RoomDiagnosticDetails));
    }

    private void RecordRoomFailure(Exception ex)
    {
        _roomDiagnostic?.SetFailure(ex);
        Notify(nameof(RoomDiagnosticDetails));
    }

    private void CopyRoomDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (_roomDiagnostic is not { } diagnostic) return;
        try
        {
            var report = diagnostic.Format(AppVersionText);
            if (RoomLatencyDiagnostic is { Length: > 0 } latency) report += Environment.NewLine + Environment.NewLine + latency;
            Clipboard.SetText(report);
            StatusMessage = "已复制联机诊断。";
        }
        catch (COMException) { StatusMessage = "剪贴板暂时不可用，请重试。"; }
    }
    public bool MultiplayerActive => _multiplayerCancellation is not null;
    public bool CanEditRelay => !MultiplayerActive && !IsBusy;
    public bool CanChooseHostVersion => CanEditRelay && !IsGameRunning;
    public IReadOnlyList<GameVersion> HostRoomVersions => _runningGame is { } running
        ? [running] : Versions.Where(IsHostable).ToArray();
    public GameVersion? HostRoomVersion
    {
        get => _runningGame ?? (SelectedVersion is { } selected && IsHostable(selected)
            ? selected : Versions.FirstOrDefault(IsHostable));
        set
        {
            if (CanChooseHostVersion && value is not null && Versions.Contains(value) && IsHostable(value))
                SelectedVersion = value;
        }
    }
    public bool CanHostRoom => CanEditRelay && !_runningForGuest
        && (!IsGameRunning || _runningGame is not null) && HostRoomVersion is { } version && IsHostable(version);
    public bool CanJoinRoom => (CanEditRelay || _retryJoin && _multiplayerCancellation is { IsCancellationRequested: false } && !_hostingRoom && !IsBusy)
        && (!IsGameRunning || _runningGame is not null) && InviteInput.Trim().Length != 0;
    public string HostRoomButtonText => IsGameRunning ? _retryHost ? "重试开房" : "创建房间" : "启动并创建房间";
    public string JoinRoomButtonText => _retryJoin ? "重试加入" : IsGameRunning ? "加入房间" : "加入并启动";
    public string LanRoomStatus => _runningForGuest ? "请先退出正在加入的游戏，再创建自己的房间。"
        : HostRoomVersion is not { } version || !IsHostable(version)
        ? "需要可启动的 Java 版 1.20 或以上版本。"
        : _runningGame is null ? ""
        : _lanPort is null ? "等待游戏开放局域网。"
        : "已识别局域网房间。";

    private void NotifyMultiplayer() => Notify(nameof(MultiplayerActive), nameof(CanEditRelay), nameof(CanHostRoom),
        nameof(CanChooseHostVersion), nameof(HostRoomVersion),
        nameof(CanJoinRoom), nameof(HostRoomButtonText), nameof(JoinRoomButtonText), nameof(LanRoomStatus),
        nameof(CanChooseSharedRelay), nameof(HostTransportHint),
        nameof(CanManageSaves), nameof(CanShareModpack), nameof(CanCompareModpack), nameof(ActivityText), nameof(ActivityIsReady));

    private static RoomGame Describe(GameVersion version) =>
        new(version.ModpackMinecraft ?? version.Id, version.ModpackName, version.ModpackVersion, version.ModpackLoader);

    private static bool IsHostable(GameVersion version) => version.IsValid && (version.ModpackName is not null || version.InheritsFrom is null)
        && Describe(version).IsSupported;

    private void HostVersionList_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ListBox { IsVisible: true } list)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (list.IsVisible && list.SelectedItem is { } version) list.ScrollIntoView(version);
            });
    }

    private static Task<string> RoomModsAsync(GameVersion version, CancellationToken ct) =>
        Task.Run(() => ModpackComparison.FingerprintAsync(
            Path.GetDirectoryName(Path.GetDirectoryName(version.VersionDirectory))!, version, ct), ct);

    private static string DefaultRelayEndpoint()
    {
        var url = Environment.GetEnvironmentVariable("MCL_RELAY_URL");
        if (!string.IsNullOrWhiteSpace(url)) return url.Trim();
        var path = Path.Combine(AppContext.BaseDirectory, "relay-url.txt");
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 4096) return "";
            using var reader = new StreamReader(file);
            return reader.ReadToEnd().Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private void OnLanPortChanged(int? port)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _runningGame is null) return;
            if (_hostingRoom && _lanPort is not null && _lanPort != port)
            {
                MultiplayerStage = "局域网已关闭";
                EndMultiplayer("局域网房间已关闭，请重新开放后再开房。");
            }
            _lanPort = port;
            if (port is { } ready) _lanReady?.TrySetResult(ready);
            NotifyMultiplayer();
        });
    }

    private CancellationTokenSource BeginMultiplayer(bool hosting)
    {
        _hostingRoom = hosting;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _multiplayerCancellation = cancellation;
        NotifyMultiplayer();
        return cancellation;
    }

    private void EndMultiplayer(string status)
    {
        if (_multiplayerCancellation is not { IsCancellationRequested: false } cancellation) return;
        MultiplayerStatus = status;
        cancellation.Cancel();
    }

    private void ResetMultiplayer()
    {
        StopLatency();
        _multiplayerCancellation = null;
        _hostingRoom = false;
        _guestUsesGame = false;
        _lanReady = null;
        RoomCode = "";
        NotifyMultiplayer();
    }

    private async void HostRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!CanHostRoom || HostRoomVersion is not { } version) return;
        if (_runningGame is null) SelectedVersion = version;
        using var cancellation = BeginMultiplayer(hosting: true);
        StartRoomDiagnostic(hosting: true, UseIpv6Direct ? RoomRoute.Ipv6 : UseSharedRelay ? RoomRoute.SharedRelay : RoomRoute.Cloudflare);
        MultiplayerStage = "准备开房";
        HostedRelay? automatic = null;
        try
        {
            var directAddress = UseIpv6Direct
                ? DirectMultiplayer.FindLocalAddress() ?? throw new InvalidDataException("本机未找到可用的公网 IPv6 地址。请检查网络，或改用中转。")
                : null;
            var sharedClient = directAddress is null && UseSharedRelay ? new MultiplayerClient(RelayEndpoint) : null;
            _lanReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_lanPort is { } ready) _lanReady.TrySetResult(ready);
            if (_runningGame is null)
            {
                IsBusy = true;
                MultiplayerStage = "启动游戏";
                MultiplayerStatus = $"正在启动 {version.DisplayName}…";
                try { await LaunchGameAsync(version, cancellation.Token); }
                finally { IsBusy = false; }
                if (!_launcher.IsRunning) throw new IOException("游戏未能保持运行。请查看运行日志后重试。");
            }
            MultiplayerStage = "识别局域网房间";
            MultiplayerStatus = _lanPort is null ? "请进入单人存档，按 Esc 选择「对局域网开放」。MCL 识别后会继续开房。" : "已识别当前存档的局域网房间。";
            var port = await _lanReady.Task.WaitAsync(cancellation.Token);
            MultiplayerStage = "核对房主模组";
            MultiplayerStatus = "正在读取当前游戏目录的模组文件…";
            var game = Describe(version) with { ModsSha256 = await RoomModsAsync(version, cancellation.Token) };
            if (directAddress is not null)
            {
                MultiplayerStage = "准备 IPv6 直连";
                var samples = new Progress<double?>(sample => ObserveDirectGuestLatency(cancellation, sample));
                var directRoom = DirectMultiplayer.Host(game, directAddress, port, cancellation.Token, samples);
                StartLatency(LatencyTarget.DirectHost);
                RoomCode = directRoom.Invite;
                _retryHost = false;
                MultiplayerStage = "IPv6 房间已开放";
                MultiplayerStatus = $"本机已开始监听 TCP {directRoom.Port}，等待好友从外网测试。邀请码包含你的 IPv6 地址，请只发给好友。";
                await directRoom.Completion;
                return;
            }
            MultiplayerStage = "连接中转";
            if (sharedClient is null)
            {
                var progress = new Progress<string>(message =>
                {
                    if (ReferenceEquals(_multiplayerCancellation, cancellation) && !cancellation.IsCancellationRequested)
                        MultiplayerStatus = message;
                });
                MultiplayerStatus = "正在检查和准备本机联机组件…";
                automatic = await Task.Run(() => new AutomaticRoomHost(_store.DataDirectory)
                    .StartAsync(progress, cancellation.Token), cancellation.Token);
            }
            var client = sharedClient ?? new MultiplayerClient(automatic!.Relay.AbsoluteUri);
            MultiplayerStatus = "正在通过中转服务注册房间…";
            var room = await client.HostAsync(game, port, cancellation.Token, automatic?.Key, () => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_multiplayerCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    MultiplayerStage = "已转发好友的连接";
            }));
            if (!cancellation.IsCancellationRequested)
            {
                var probe = automatic is null ? client : new MultiplayerClient(automatic.Public.AbsoluteUri);
                _ = MonitorLatencyAsync(cancellation, automatic is null ? LatencyTarget.Relay : LatencyTarget.PublicTunnelLoop,
                    probe.MeasureLatencyAsync);
                RoomCode = automatic is null ? room.Code : RoomInvite.Create(automatic.Public, room.Code);
                _retryHost = false;
                MultiplayerStage = "房间已开放";
                MultiplayerStatus = "房间已开启。复制邀请码发给好友，保持 MCL 和游戏运行。";
                if (automatic is { PublicAddressVerified: false })
                {
                    const string note = "Cloudflare 已确认隧道在线，房主电脑暂时无法解析公网地址。请好友试用邀请码。";
                    MultiplayerStatus = note;
                    _log.Warn(note);
                }
            }
            if (automatic is not null && await Task.WhenAny(room.Completion, automatic.Completion) == automatic.Completion
                && !cancellation.IsCancellationRequested)
            {
                MultiplayerStatus = "本机中转或公网隧道已退出，请重新开房。";
                _retryHost = true;
                MultiplayerStage = "中转已断开";
                cancellation.Cancel();
            }
            await room.Completion;
            if (!cancellation.IsCancellationRequested)
            {
                _retryHost = true;
                MultiplayerStage = "中转已断开";
                MultiplayerStatus = "联机服务已断开，可以重新开房。";
            }
        }
        catch (Exception ex) when (IsMultiplayerError(ex))
        {
            if (!cancellation.IsCancellationRequested)
            {
                _retryHost = true;
                MultiplayerStage += "失败";
                RecordRoomFailure(ex);
                MultiplayerStatus = MultiplayerError(ex);
                _log.Warn($"创建联机房间失败：{MultiplayerStatus}");
            }
        }
        finally
        {
            cancellation.Cancel();
            automatic?.Dispose();
            ResetMultiplayer();
        }
    }

    private async void JoinRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!CanJoinRoom) return;
        if (MultiplayerActive)
        {
            _rejoinRequested = true;
            EndMultiplayer("正在重新连接当前房间…");
            NotifyMultiplayer();
            return;
        }
        var runningGame = _runningGame;
        var input = InviteInput.Trim();
        var route = input.StartsWith("MCL6/", StringComparison.OrdinalIgnoreCase) ? RoomRoute.Ipv6
            : input.StartsWith("MCL1/", StringComparison.OrdinalIgnoreCase) ? RoomRoute.Cloudflare
            : RoomProtocol.IsToken(input) ? RoomRoute.SharedRelay : RoomRoute.Unknown;
        StartRoomDiagnostic(hosting: false, route);
        RoomInvite invite;
        try { invite = RoomInvite.Read(input, RelayEndpoint); }
        catch (InvalidDataException ex)
        {
            RecordRoomFailure(ex);
            MultiplayerStage = "邀请码有误";
            MultiplayerStatus = ex.Message;
            return;
        }
        using var cancellation = BeginMultiplayer(hosting: false);
        _guestUsesGame = runningGame is not null;
        GuestTunnel? tunnel = null;
        Task? discovery = null;
        var preparing = true;
        IsBusy = true;
        MultiplayerStage = "查找好友房间";
        MultiplayerStatus = "正在查找好友房间…";
        try
        {
            var direct = invite.Direct is { } endpoint ? new DirectMultiplayer(endpoint, invite.Code) : null;
            var client = direct is null ? new MultiplayerClient(invite.Address) : null;
            if (direct is not null)
            {
                MultiplayerStage = "检查 IPv6 直连";
                MultiplayerStatus = "正在直连房主并验证邀请码…";
            }
            var game = direct is null ? await client!.FindAsync(invite.Code, cancellation.Token) : await direct.FindAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            MultiplayerStage = "检查版本和模组";
            MultiplayerStatus = "正在核对 Minecraft 版本、整合包、加载器和模组文件…";
            var version = await ResolveJoiningVersionAsync(game, runningGame, cancellation.Token);
            if (version.ModpackName is null && version.InheritsFrom is not null)
                throw new InvalidDataException("请选择原版游戏或由 MCL 导入的整合包。");
            void ConnectionFailed() => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_multiplayerCancellation, cancellation) && !cancellation.IsCancellationRequested)
                {
                    _retryJoin = true;
                    MultiplayerStage = "连接房间失败";
                    MultiplayerStatus = direct is null
                        ? "请确认房间仍开放，点「重试加入」即可用当前邀请码再连接。"
                        : "IPv6 游戏连接失败。请确认房主仍开放存档、房间未关闭，再点「重试加入」。";
                    NotifyMultiplayer();
                }
            });
            void Connected() => Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_multiplayerCancellation, cancellation) || cancellation.IsCancellationRequested) return;
                _retryJoin = false;
                MultiplayerStage = "已连通房主游戏";
                MultiplayerStatus = direct is null ? "中转已连通。若游戏内仍无法进入，请查看 Minecraft 的提示。"
                    : "IPv6 已直连房主。若游戏内仍无法进入，请查看 Minecraft 的提示。";
                NotifyMultiplayer();
            });
            tunnel = direct is null
                ? client!.StartGuest(invite.Code, ConnectionFailed, cancellation.Token, Connected)
                : direct.StartGuest(ConnectionFailed, cancellation.Token, Connected);
            if (direct is null)
                _ = MonitorLatencyAsync(cancellation, route == RoomRoute.Cloudflare
                    ? LatencyTarget.PublicTunnel : LatencyTarget.Relay, client!.MeasureLatencyAsync);
            else
            {
                var probe = direct.CreateLatencyProbe();
                _ = MonitorLatencyAsync(cancellation, LatencyTarget.DirectGuest, probe.MeasureAsync, probe);
            }
            if (runningGame is null)
            {
                SelectedVersion = version;
                MultiplayerStage = "启动游戏";
                MultiplayerStatus = $"正在启动 {version.DisplayName}…";
                await LaunchGameAsync(version, cancellation.Token, joiningFriend: true);
                _guestUsesGame = true;
            }
            if (!_launcher.IsRunning)
            {
                EndMultiplayer("游戏已退出，联机已结束。");
                return;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            _retryJoin = false;
            preparing = false;
            IsBusy = false;
            if (!cancellation.IsCancellationRequested)
            {
                MultiplayerStage = "等待从游戏内加入";
                MultiplayerStatus = $"请在 Minecraft 的「多人游戏」中选择局域网列表里的「{GuestTunnel.LanRoomName}」。"
                    + (game.ModsSha256 is null ? " 此房间未提供模组校验信息。" : " 版本、加载器和模组已核对。");
            }
            discovery = tunnel.AdvertiseLanRoomAsync(cancellation.Token);
            var completed = await Task.WhenAny(tunnel.Completion, discovery);
            if (completed == discovery && discovery.IsFaulted)
                throw new IOException("无法通知 Minecraft 显示本机的局域网房间。请重新加入房间。", discovery.Exception);
            await completed;
        }
        catch (Exception ex) when (IsMultiplayerError(ex) || ex is InvalidOperationException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                _retryJoin = true;
                MultiplayerStage += "失败";
                RecordRoomFailure(ex);
                MultiplayerStatus = MultiplayerError(ex);
                _log.Warn($"加入好友房间失败：{MultiplayerStatus}");
            }
        }
        finally
        {
            cancellation.Cancel();
            if (tunnel is not null)
            {
                try { await tunnel.Completion; }
                catch (Exception ex) when (IsMultiplayerError(ex)) { }
            }
            if (discovery is not null)
            {
                try { await discovery; }
                catch (Exception ex) when (IsMultiplayerError(ex)) { }
            }
            if (preparing) IsBusy = false;
            var rejoin = _rejoinRequested;
            _rejoinRequested = false;
            ResetMultiplayer();
            if (rejoin && !_closing) _ = Dispatcher.BeginInvoke(() => JoinRoom_Click(this, new RoutedEventArgs()));
        }
    }

    private async Task<GameVersion> ResolveJoiningVersionAsync(RoomGame room, GameVersion? runningGame, CancellationToken ct)
    {
        var required = room.Pack is null ? $"Minecraft {room.Minecraft} 原版"
            : $"Minecraft {room.Minecraft} · 整合包「{room.Pack}」{room.PackVersion} · {room.Loader ?? "原版"}";
        var identity = room with { ModsSha256 = null };
        if (runningGame is not null)
        {
            if (Describe(runningGame) != identity)
                throw new InvalidDataException($"当前运行的是「{runningGame.DisplayName}」。房间需要 {required}。请先退出当前游戏，再加入房间。");
        }
        var candidates = runningGame is null
            ? Versions.Where(candidate => candidate.IsValid && Describe(candidate) == identity).OrderByDescending(candidate => candidate == SelectedVersion).ToArray()
            : [runningGame];
        if (candidates.Length == 0) throw new InvalidDataException($"请先安装房间需要的 {required}。");
        if (room.ModsSha256 is null) return candidates[0];
        foreach (var candidate in candidates)
        {
            var hash = await RoomModsAsync(candidate, ct);
            if (hash.Equals(room.ModsSha256, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        throw new InvalidDataException("本机模组和房主不同。请让房主发来整合包分享 ZIP，再点「对比好友分享包」查看缺少、多出或文件不同的模组。");
    }

    private static bool IsMultiplayerError(Exception ex) => ex is InvalidDataException or IOException or
        OperationCanceledException or SocketException or WebSocketException or HttpRequestException or JsonException
        or InvalidOperationException or UnauthorizedAccessException or Win32Exception or NotSupportedException
        or NetworkInformationException or CryptographicException;

    private static string MultiplayerError(Exception ex) => ex switch
    {
        HttpRequestException or WebSocketException or SocketException or OperationCanceledException or JsonException =>
            "联机服务连接失败。请检查网络和联机服务地址。",
        _ => ex.Message
    };

    private void CopyRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!HasRoomCode) return;
        try
        {
            Clipboard.SetText(RoomCode);
            MultiplayerStatus = UseIpv6Direct ? "已复制 IPv6 邀请码，其中包含你的 IPv6 地址，请只发给好友。"
                : "已复制邀请码，可以粘贴发给好友。";
        }
        catch (COMException) { MultiplayerStatus = "剪贴板暂时不可用，可以选中邀请码手动复制。"; }
    }

    private void CloseRoom_Click(object sender, RoutedEventArgs e)
    {
        MultiplayerStage = "联机已结束";
        EndMultiplayer(_hostingRoom ? "房间已关闭。" : "已结束联机。");
    }

    public void OpenMultiplayer() => MultiplayerNav.IsChecked = true;
}
