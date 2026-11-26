using System.Net.Http;
using System.ComponentModel;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
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
    private TaskCompletionSource<int>? _lanReady;
    private string _relayEndpoint = "";
    private string _inviteInput = "";
    private string _roomCode = "";
    private string _multiplayerStatus = "";

    public string RelayEndpoint
    {
        get => _relayEndpoint;
        set { if (Set(ref _relayEndpoint, value)) { MarkDirty(); NotifyMultiplayer(); } }
    }

    public string InviteInput
    {
        get => _inviteInput;
        set { if (Set(ref _inviteInput, value)) Notify(nameof(CanJoinRoom)); }
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
        }
    }
    public bool UseSharedRelay
    {
        get => _useSharedRelay;
        set { if (Set(ref _useSharedRelay, value)) NotifyMultiplayer(); }
    }
    public string MultiplayerStatus { get => _multiplayerStatus; private set => Set(ref _multiplayerStatus, value); }
    public bool MultiplayerActive => _multiplayerCancellation is not null;
    public bool CanEditRelay => !MultiplayerActive && !IsBusy;
    public bool CanHostRoom => CanEditRelay && !_runningForGuest
        && (IsGameRunning ? _runningGame : SelectedVersion) is { IsValid: true } game && Describe(game).IsSupported;
    public bool CanJoinRoom => CanEditRelay && (!IsGameRunning || _runningGame is not null) && InviteInput.Trim().Length != 0;
    public string JoinRoomButtonText => IsGameRunning ? "加入房间" : "加入并启动";
    public string LanRoomStatus => _runningForGuest ? "请先退出正在加入的游戏，再创建自己的房间。"
        : (_runningGame ?? SelectedVersion) is not { IsValid: true } version
        ? "请先在「游戏版本」里选择可启动的版本。"
        : !Describe(version).IsSupported ? "好友联机支持 Minecraft Java 1.20 及以上。"
        : _runningGame is null ? $"将启动 {version.DisplayName}。"
        : _lanPort is null ? "等待游戏开放局域网。"
        : "已识别局域网房间。";

    private void NotifyMultiplayer() => Notify(nameof(MultiplayerActive), nameof(CanEditRelay), nameof(CanHostRoom),
        nameof(CanJoinRoom), nameof(JoinRoomButtonText), nameof(LanRoomStatus));

    private static RoomGame Describe(GameVersion version) =>
        new(version.ModpackMinecraft ?? version.Id, version.ModpackName, version.ModpackVersion, version.ModpackLoader);

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
                EndMultiplayer("局域网房间已关闭，请重新开放后再开房。");
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
        _multiplayerCancellation = null;
        _hostingRoom = false;
        _guestUsesGame = false;
        _lanReady = null;
        RoomCode = "";
        NotifyMultiplayer();
    }

    private async void HostRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!CanHostRoom || (_runningGame ?? SelectedVersion) is not { } version) return;
        using var cancellation = BeginMultiplayer(hosting: true);
        HostedRelay? automatic = null;
        try
        {
            var sharedClient = UseSharedRelay ? new MultiplayerClient(RelayEndpoint) : null;
            _lanReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_lanPort is { } ready) _lanReady.TrySetResult(ready);
            if (_runningGame is null)
            {
                IsBusy = true;
                MultiplayerStatus = $"正在启动 {version.DisplayName}…";
                try { await LaunchGameAsync(version, cancellation.Token, detectJava: true); }
                finally { IsBusy = false; }
                if (!_launcher.IsRunning) throw new IOException("游戏未能保持运行。请查看运行日志后重试。");
            }
            MultiplayerStatus = "请进入单人存档，按 Esc 选择「对局域网开放」。MCL 识别后会继续开房。";
            var port = await _lanReady.Task.WaitAsync(cancellation.Token);
            if (sharedClient is null)
            {
                var progress = new Progress<string>(message =>
                {
                    if (!cancellation.IsCancellationRequested) MultiplayerStatus = message;
                });
                MultiplayerStatus = "正在检查和准备本机联机组件…";
                automatic = await Task.Run(() => new AutomaticRoomHost(_store.DataDirectory)
                    .StartAsync(progress, cancellation.Token), cancellation.Token);
            }
            var client = sharedClient ?? new MultiplayerClient(automatic!.Relay.AbsoluteUri);
            var room = await client.HostAsync(Describe(version), port, cancellation.Token, automatic?.Key);
            if (!cancellation.IsCancellationRequested)
            {
                RoomCode = automatic is null ? room.Code : RoomInvite.Create(automatic.Public, room.Code);
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
                cancellation.Cancel();
            }
            await room.Completion;
            if (!cancellation.IsCancellationRequested) MultiplayerStatus = "联机服务已断开，可以重新开房。";
        }
        catch (Exception ex) when (IsMultiplayerError(ex))
        {
            if (!cancellation.IsCancellationRequested)
            {
                MultiplayerStatus = MultiplayerError(ex);
                _log.Warn($"创建联机房间失败：{MultiplayerStatus}");
            }
        }
        finally
        {
            automatic?.Dispose();
            ResetMultiplayer();
        }
    }

    private async void JoinRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!CanJoinRoom) return;
        var runningGame = _runningGame;
        RoomInvite invite;
        try { invite = RoomInvite.Read(InviteInput, RelayEndpoint); }
        catch (InvalidDataException ex) { MultiplayerStatus = ex.Message; return; }
        using var cancellation = BeginMultiplayer(hosting: false);
        _guestUsesGame = runningGame is not null;
        GuestTunnel? tunnel = null;
        Task? discovery = null;
        var preparing = true;
        IsBusy = true;
        MultiplayerStatus = "正在查找好友房间…";
        try
        {
            var client = new MultiplayerClient(invite.Address);
            var game = await client.FindAsync(invite.Code, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var version = ResolveJoiningVersion(game, runningGame);
            if (version.ModpackName is null && version.InheritsFrom is not null)
                throw new InvalidDataException("请选择原版游戏或由 MCL 导入的整合包。");
            tunnel = client.StartGuest(invite.Code, () => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_multiplayerCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    MultiplayerStatus = "连接房间失败。请确认房间仍开放，再重新加入。";
            }), cancellation.Token);
            if (runningGame is null)
            {
                SelectedVersion = version;
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
            InviteInput = "";
            preparing = false;
            IsBusy = false;
            if (!cancellation.IsCancellationRequested)
                MultiplayerStatus = $"请在 Minecraft 的「多人游戏」中选择局域网列表里的「{GuestTunnel.LanRoomName}」。";
            discovery = tunnel.AdvertiseLanRoomAsync(cancellation.Token);
            var completed = await Task.WhenAny(tunnel.Completion, discovery);
            if (completed == discovery && discovery.IsFaulted)
                throw new IOException("无法通知 Minecraft 显示本机的局域网房间。请重新加入房间。", discovery.Exception);
            await completed;
        }
        catch (Exception ex) when (IsMultiplayerError(ex) || ex is InvalidOperationException)
        {
            if (!cancellation.IsCancellationRequested) MultiplayerStatus = MultiplayerError(ex);
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
            ResetMultiplayer();
        }
    }

    private GameVersion ResolveJoiningVersion(RoomGame room, GameVersion? runningGame)
    {
        var required = room.Pack is null ? $"Minecraft {room.Minecraft} 原版" : $"整合包「{room.Pack}」{room.PackVersion}";
        if (runningGame is not null)
        {
            if (Describe(runningGame) != room)
                throw new InvalidDataException($"当前运行的是「{runningGame.DisplayName}」。房间需要 {required}。请先退出当前游戏，再加入房间。");
            return runningGame;
        }

        if (SelectedVersion is { IsValid: true } selected && Describe(selected) == room) return selected;
        return Versions.FirstOrDefault(candidate => candidate.IsValid && Describe(candidate) == room)
            ?? throw new InvalidDataException($"请先安装房间需要的 {required}。");
    }

    private static bool IsMultiplayerError(Exception ex) => ex is InvalidDataException or IOException or
        OperationCanceledException or SocketException or WebSocketException or HttpRequestException or JsonException
        or InvalidOperationException or UnauthorizedAccessException or Win32Exception or NotSupportedException;

    private static string MultiplayerError(Exception ex) => ex switch
    {
        HttpRequestException or WebSocketException or SocketException or OperationCanceledException or JsonException =>
            "联机服务连接失败。请检查网络和联机服务地址。",
        _ => ex.Message
    };

    private void CopyRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!HasRoomCode) return;
        try { Clipboard.SetText(RoomCode); MultiplayerStatus = "已复制邀请码，可以粘贴发给好友。"; }
        catch (COMException) { MultiplayerStatus = "剪贴板暂时不可用，可以选中邀请码手动复制。"; }
    }

    private void CloseRoom_Click(object sender, RoutedEventArgs e) => EndMultiplayer(_hostingRoom ? "房间已关闭。" : "已结束联机。");

    public void OpenMultiplayer() => MultiplayerNav.IsChecked = true;
}
