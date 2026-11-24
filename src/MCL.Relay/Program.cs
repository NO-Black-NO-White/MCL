using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using MCL.Multiplayer;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:28470");
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20),
    KeepAliveTimeout = TimeSpan.FromSeconds(15)
});

var rooms = new ConcurrentDictionary<string, RelayRoom>(StringComparer.Ordinal);
var tickets = new ConcurrentDictionary<string, GuestConnection>(StringComparer.Ordinal);
var capacity = new SemaphoreSlim(64);
var stopping = app.Lifetime.ApplicationStopping;
var endpoints = app.MapGroup(RoomProtocol.Prefix);
endpoints.MapGet("/health", () => Results.Ok());

endpoints.MapGet("/host", async (HttpContext request) =>
{
    if (!AcceptsSocket(request)) return;
    if (!capacity.Wait(0)) { request.Response.StatusCode = 503; return; }
    var code = RoomProtocol.Token();
    using var room = new RelayRoom(request.RequestAborted, stopping);
    try
    {
        using var socket = await request.WebSockets.AcceptWebSocketAsync();
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(room.Token);
        handshake.CancelAfter(TimeSpan.FromSeconds(10));
        var input = await RoomProtocol.ReadTextAsync(socket, handshake.Token);
        if (input is null || JsonSerializer.Deserialize<RoomGame>(input, RoomProtocol.Json) is not { IsSupported: true } game)
            return;
        room.Game = game;
        if (!rooms.TryAdd(code, room)) return;
        await RoomProtocol.SendTextAsync(socket, code, handshake.Token);

        var read = RoomProtocol.ReadTextAsync(socket, room.Token);
        var send = SendTicketsAsync(socket, room);
        await Task.WhenAny(read, send);
        await room.CancelAsync();
        await Task.WhenAll(read, send);
    }
    catch (Exception ex) when (Ended(ex)) { }
    finally
    {
        rooms.TryRemove(new KeyValuePair<string, RelayRoom>(code, room));
        capacity.Release();
    }
});

endpoints.MapPost("/info", (HttpContext request) =>
    FindRoom(request) is { Game: { } game } ? Results.Json(game, RoomProtocol.Json) : Results.NotFound());

endpoints.MapGet("/guest", async (HttpContext request) =>
{
    if (!AcceptsSocket(request)) return;
    var room = FindRoom(request);
    if (room is null) { request.Response.StatusCode = 404; return; }
    if (!room.Slots.Wait(0)) { request.Response.StatusCode = 429; return; }

    var ticket = RoomProtocol.Token();
    using var session = CancellationTokenSource.CreateLinkedTokenSource(request.RequestAborted, room.Token);
    var connection = new GuestConnection(session.Token);
    try
    {
        using var guest = await request.WebSockets.AcceptWebSocketAsync();
        if (!tickets.TryAdd(ticket, connection) || !room.Tickets.Writer.TryWrite(ticket)) return;
        var host = await connection.Host.Task.WaitAsync(TimeSpan.FromSeconds(8), session.Token);
        await RoomProtocol.SendTextAsync(guest, "ready", session.Token);
        await RoomProtocol.BridgeAsync(host, guest, session.Token);
    }
    catch (Exception ex) when (Ended(ex)) { }
    finally
    {
        tickets.TryRemove(ticket, out _);
        connection.Finished.TrySetResult();
        room.Slots.Release();
    }
});

endpoints.MapGet("/connect", async (HttpContext request) =>
{
    if (!AcceptsSocket(request)) return;
    var ticket = Credential(request);
    if (ticket is null || !tickets.TryRemove(ticket, out var connection))
    {
        request.Response.StatusCode = 404;
        return;
    }
    try
    {
        using var host = await request.WebSockets.AcceptWebSocketAsync();
        if (connection.Token.IsCancellationRequested) return;
        connection.Host.TrySetResult(host);
        await connection.Finished.Task.WaitAsync(request.RequestAborted);
    }
    catch (Exception ex) when (Ended(ex)) { }
});

app.Run();

RelayRoom? FindRoom(HttpContext request)
{
    var code = Credential(request);
    return code is not null && rooms.TryGetValue(code, out var room) && !room.Token.IsCancellationRequested ? room : null;
}

static string? Credential(HttpContext request)
{
    var value = request.Request.Headers.Authorization.ToString();
    return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && RoomProtocol.IsToken(value[7..]) ? value[7..].ToLowerInvariant() : null;
}

static bool AcceptsSocket(HttpContext request)
{
    if (request.WebSockets.IsWebSocketRequest && !request.Request.Headers.ContainsKey("Origin")) return true;
    request.Response.StatusCode = 400;
    return false;
}

static async Task SendTicketsAsync(WebSocket socket, RelayRoom room)
{
    await foreach (var ticket in room.Tickets.Reader.ReadAllAsync(room.Token))
        await RoomProtocol.SendTextAsync(socket, ticket, room.Token);
}

static bool Ended(Exception ex) =>
    ex is WebSocketException or OperationCanceledException or IOException or JsonException or TimeoutException or System.Text.DecoderFallbackException;

internal sealed class RelayRoom : IDisposable
{
    private readonly CancellationTokenSource _lifetime;

    internal RelayRoom(CancellationToken request, CancellationToken stopping)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(request, stopping);
        Token = _lifetime.Token;
    }

    internal CancellationToken Token { get; }
    internal Task CancelAsync() => _lifetime.CancelAsync();
    internal RoomGame? Game { get; set; }
    internal SemaphoreSlim Slots { get; } = new(RoomProtocol.MaximumGuests * 2);
    internal Channel<string> Tickets { get; } = Channel.CreateBounded<string>(RoomProtocol.MaximumGuests * 2);

    public void Dispose()
    {
        _lifetime.Cancel();
        Tickets.Writer.TryComplete();
        _lifetime.Dispose();
    }
}

internal sealed class GuestConnection(CancellationToken token)
{
    internal CancellationToken Token { get; } = token;
    internal TaskCompletionSource<WebSocket> Host { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
