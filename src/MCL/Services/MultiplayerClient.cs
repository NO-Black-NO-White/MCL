using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MCL.Multiplayer;

namespace MCL.Services;

internal sealed record HostRoom(string Code, Task Completion);
internal sealed record GuestTunnel(int Port, Task Completion)
{
    internal const string LanRoomName = "MCL 好友房间";
    internal const int LanDiscoveryPort = 4445;

    internal static GuestTunnel Start(Func<NetworkStream, CancellationToken, Task> connect,
        Action connectionFailed, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(RoomProtocol.MaximumGuests * 2);
        return new(((IPEndPoint)listener.LocalEndpoint).Port, ListenAsync(listener, connect, connectionFailed, ct));
    }

    private static async Task ListenAsync(TcpListener listener, Func<NetworkStream, CancellationToken, Task> connect,
        Action connectionFailed, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var active = new List<Task>();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var game = await listener.AcceptTcpClientAsync(lifetime.Token);
                active.RemoveAll(item => item.IsCompletedSuccessfully);
                if (active.Count < RoomProtocol.MaximumGuests * 2)
                    active.Add(ConnectAsync(game, connect, connectionFailed, lifetime.Token));
                else game.Dispose();
            }
        }
        finally
        {
            listener.Stop();
            await lifetime.CancelAsync();
            await Task.WhenAll(active);
        }
    }

    private static async Task ConnectAsync(TcpClient game, Func<NetworkStream, CancellationToken, Task> connect,
        Action connectionFailed, CancellationToken ct)
    {
        using (game)
        {
            try
            {
                game.NoDelay = true;
                await connect(game.GetStream(), ct);
            }
            catch (Exception ex) when (ex is SocketException or WebSocketException or IOException or OperationCanceledException
                or System.Security.Authentication.AuthenticationException or InvalidDataException)
            {
                if (!ct.IsCancellationRequested) connectionFailed();
            }
        }
    }

    internal async Task AdvertiseLanRoomAsync(CancellationToken ct)
    {
        // Keep the advertisement on the guest PC, where the proxy also listens.
        using var discovery = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var destination = new IPEndPoint(IPAddress.Loopback, LanDiscoveryPort);
        var message = Encoding.UTF8.GetBytes(FormattableString.Invariant($"[MOTD]{LanRoomName}[/MOTD][AD]{Port}[/AD]"));
        while (true)
        {
            await discovery.SendAsync(message.AsMemory(), destination, ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
        }
    }
}

internal sealed class MultiplayerClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly Uri _endpoint;

    internal MultiplayerClient(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var address)
            || (address.Scheme != "https" && !(address.Scheme == "http" && address.IsLoopback))
            || address.AbsolutePath != "/" || address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0)
            throw new InvalidDataException("请填写 HTTPS 联机服务地址。测试时可使用本机 HTTP 地址。");
        _endpoint = address;
    }

    internal async Task<HostRoom> HostAsync(RoomGame game, int port, CancellationToken ct, string? hostKey = null, Action? guestConnected = null)
    {
        if (!game.IsSupported || port is <= 0 or > 65535) throw new InvalidDataException("当前游戏无法创建联机房间。");
        using var timeout = Deadline(ct);
        var control = await ConnectAsync("/host", hostKey, timeout.Token);
        try
        {
            await RoomProtocol.SendTextAsync(control, JsonSerializer.Serialize(game, RoomProtocol.Json), timeout.Token);
            var code = await RoomProtocol.ReadTextAsync(control, timeout.Token);
            if (!RoomProtocol.IsToken(code)) throw new IOException("中转服务未能创建房间。");
            return new HostRoom(code!, RunHostAsync(control, port, guestConnected, ct));
        }
        catch
        {
            control.Dispose();
            throw;
        }
    }

    internal async Task<RoomGame> FindAsync(string code, CancellationToken ct)
    {
        using var timeout = Deadline(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("/info"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", code);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("房间不存在或已关闭，请向房主索取新的邀请码。");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[2049];
        var count = await stream.ReadAtLeastAsync(buffer.AsMemory(), buffer.Length, false, timeout.Token);
        if (count >= buffer.Length || JsonSerializer.Deserialize<RoomGame>(buffer.AsSpan(0, count), RoomProtocol.Json) is not { IsSupported: true } game)
            throw new InvalidDataException("联机服务返回的房间信息无效。");
        return game;
    }

    internal async Task<double> MeasureLatencyAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var response = await Http.GetAsync(Endpoint("/health"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    internal GuestTunnel StartGuest(string code, Action connectionFailed, CancellationToken ct, Action? connected = null) =>
        GuestTunnel.Start(async (game, token) =>
        {
            using var timeout = Deadline(token);
            using var socket = await ConnectAsync("/guest", code, timeout.Token);
            if (await RoomProtocol.ReadTextAsync(socket, timeout.Token) != "ready")
                throw new IOException("房主的游戏连接不可用。");
            connected?.Invoke();
            await RoomProtocol.BridgeAsync(socket, game, token);
        }, connectionFailed, ct);

    private async Task RunHostAsync(ClientWebSocket control, int port, Action? guestConnected, CancellationToken ct)
    {
        using (control)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var active = new List<Task>();
            try
            {
                while (await RoomProtocol.ReadTextAsync(control, lifetime.Token) is { } ticket)
                {
                    if (!RoomProtocol.IsToken(ticket)) throw new InvalidDataException("联机服务发出了无效的连接请求。");
                    active.RemoveAll(item => item.IsCompletedSuccessfully);
                    if (active.Count < RoomProtocol.MaximumGuests * 2)
                        active.Add(ConnectToWorldAsync(ticket, port, guestConnected, lifetime.Token));
                }
            }
            finally
            {
                await lifetime.CancelAsync();
                await Task.WhenAll(active);
            }
        }
    }

    private async Task ConnectToWorldAsync(string ticket, int port, Action? guestConnected, CancellationToken ct)
    {
        try
        {
            using var timeout = Deadline(ct);
            using var game = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await game.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var socket = await ConnectAsync("/connect", ticket, timeout.Token);
            guestConnected?.Invoke();
            await RoomProtocol.BridgeAsync(socket, game.GetStream(), ct);
        }
        catch (Exception ex) when (ex is SocketException or WebSocketException or IOException or OperationCanceledException) { }
    }


    private Uri Endpoint(string path) => new(_endpoint, RoomProtocol.Prefix + path);

    private async Task<ClientWebSocket> ConnectAsync(string path, string? credential, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            if (credential is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
            var endpoint = new UriBuilder(Endpoint(path)) { Scheme = _endpoint.Scheme == "https" ? "wss" : "ws" };
            await socket.ConnectAsync(endpoint.Uri, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static CancellationTokenSource Deadline(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }
}
