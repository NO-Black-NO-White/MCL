using System.Net.Http;
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
    private bool _guestLaunched;
    private string _relayEndpoint = "";
    private string _inviteInput = "";
    private string _roomCode = "";
    private string _multiplayerStatus = "邀请码在房主结束联机后失效。";

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
    public string MultiplayerStatus
    {
        get => _multiplayerStatus;
        private set { if (Set(ref _multiplayerStatus, value)) Notify(nameof(MultiplayerMessage)); }
    }
    public string MultiplayerMessage => string.IsNullOrWhiteSpace(RelayEndpoint)
        ? "还未配置联机服务。发布者配置好后，双方只需交换邀请码。"
        : MultiplayerStatus;
    public bool MultiplayerActive => _multiplayerCancellation is not null;
    public bool CanEditRelay => !MultiplayerActive && !IsBusy;
    public bool CanHostRoom => CanEditRelay && !string.IsNullOrWhiteSpace(RelayEndpoint)
        && _lanPort is not null && _runningGame is { } game && Describe(game).IsSupported;
    public bool CanJoinRoom => CanEditRelay && !IsGameRunning && !string.IsNullOrWhiteSpace(RelayEndpoint)
        && InviteInput.Trim().Length != 0;
    public string LanRoomStatus => _runningGame is null
        ? "请先用 MCL 启动游戏。"
        : !Describe(_runningGame).IsSupported ? "好友联机目前支持 Minecraft Java 1.20 及以上。"
        : _lanPort is null ? "等待游戏开放局域网。"
        : "已识别局域网房间，可以创建邀请码。";

    private void NotifyMultiplayer() => Notify(nameof(MultiplayerActive), nameof(CanEditRelay), nameof(CanHostRoom),
        nameof(CanJoinRoom), nameof(MultiplayerMessage), nameof(LanRoomStatus));

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
            if (_hostingRoom && _lanPort != port) EndMultiplayer("局域网房间已关闭，请在游戏里重新开放后再开房。");
            _lanPort = port;
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
        _guestLaunched = false;
        RoomCode = "";
        NotifyMultiplayer();
    }

    private async void HostRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!CanHostRoom || _runningGame is not { } version || _lanPort is not { } port) return;
        using var cancellation = BeginMultiplayer(hosting: true);
        MultiplayerStatus = "正在创建好友房间…";
        try
        {
            var room = await new MultiplayerClient(RelayEndpoint).HostAsync(Describe(version), port, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                RoomCode = room.Code;
                MultiplayerStatus = "房间已开启。复制邀请码发给好友，联机时请保持 MCL 和游戏运行。";
            }
            await room.Completion;
            if (!cancellation.IsCancellationRequested) MultiplayerStatus = "联机服务已断开，可以重新开房。";
        }
        catch (Exception ex) when (IsMultiplayerError(ex))
        {
            if (!cancellation.IsCancellationRequested) MultiplayerStatus = MultiplayerError(ex);
        }
        finally { ResetMultiplayer(); }
    }

    private async void JoinRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!CanJoinRoom) return;
        var code = InviteInput.Trim().ToLowerInvariant();
        if (!RoomProtocol.IsToken(code))
        {
            MultiplayerStatus = "邀请码格式有误，请完整粘贴房主复制的邀请码。";
            return;
        }
        if (UseMicrosoftAccount && (!HasMicrosoftAccount || _accountRequiresLogin))
        {
            MultiplayerStatus = "请在「账号管理」完成登录，再加入房间。";
            return;
        }
        using var cancellation = BeginMultiplayer(hosting: false);
        GuestTunnel? tunnel = null;
        var preparing = true;
        IsBusy = true;
        MultiplayerStatus = "正在查找好友房间…";
        try
        {
            var client = new MultiplayerClient(RelayEndpoint);
            var game = await client.FindAsync(code, cancellation.Token);
            var version = SelectedVersion is { IsValid: true } selected && Describe(selected) == game ? selected
                : Versions.FirstOrDefault(candidate => candidate.IsValid && Describe(candidate) == game);
            if (version is null)
                throw new InvalidDataException(game.Pack is null ? $"请先安装 Minecraft {game.Minecraft} 原版。"
                    : $"请先导入与房主一致的整合包「{game.Pack}」{game.PackVersion}。");
            if (version.ModpackName is null && version.InheritsFrom is not null)
                throw new InvalidDataException("请选择原版游戏或由 MCL 导入的整合包。");
            tunnel = client.StartGuest(code, () => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_multiplayerCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    MultiplayerStatus = "连接房间失败。请确认房间仍开放，再重新加入。";
            }), cancellation.Token);
            SelectedVersion = version;
            MultiplayerStatus = $"正在启动 {version.DisplayName} 并加入好友房间…";
            await LaunchGameAsync(version, tunnel.Port, cancellation.Token);
            _guestLaunched = true;
            if (!_launcher.IsRunning) EndMultiplayer("游戏已退出，联机已结束。");
            InviteInput = "";
            preparing = false;
            IsBusy = false;
            await tunnel.Completion;
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
            if (preparing) IsBusy = false;
            ResetMultiplayer();
        }
    }

    private static bool IsMultiplayerError(Exception ex) => ex is InvalidDataException or IOException or
        OperationCanceledException or SocketException or WebSocketException or HttpRequestException or JsonException;

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
