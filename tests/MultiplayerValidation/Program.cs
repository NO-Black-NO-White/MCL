using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using MCL.Multiplayer;
using MCL.Services;

if (args is not [var relay])
    throw new ArgumentException("Run a local MCL.Relay and provide its HTTP address.");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
using var guestLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
var ct = deadline.Token;
var service = new MultiplayerClient(relay);
var hostKey = Environment.GetEnvironmentVariable("MCL_RelayHostKey");
var sampleCode = RoomProtocol.Token();
var sampleInvite = RoomInvite.Create(new Uri("https://rose-world.trycloudflare.com"), sampleCode);
Check(RoomInvite.Read(sampleInvite, "") == new RoomInvite("https://rose-world.trycloudflare.com", sampleCode),
    "A complete invite can be used without a preconfigured endpoint");
Check(RoomInvite.Read(sampleCode, relay) == new RoomInvite(relay, sampleCode),
    "A legacy invite uses the configured relay");
foreach (var host in new[] { "trycloudflare.com", "a.trycloudflare.com.example.org", "a.trycloudflare.com:443", "a@b.trycloudflare.com" })
{
    try { RoomInvite.Read($"MCL1/{host}/{sampleCode}", ""); throw new Exception("An invalid invite address was accepted."); }
    catch (InvalidDataException) { }
}
Check(true, "An invite cannot redirect the client to unapproved hosts or ports");
var world = new TcpListener(IPAddress.Loopback, 0);
world.Start();
try
{
    var game = new RoomGame("1.20.1", "Multiplayer QA", "1", "0.16.14");
    var worldPort = ((IPEndPoint)world.LocalEndpoint).Port;
    if (hostKey is not null)
    {
        try { await service.HostAsync(game, worldPort, ct); throw new Exception("An unauthenticated host was allowed."); }
        catch (WebSocketException) { Check(true, "A managed relay rejects hosts without its private host key"); }
    }
    var room = await service.HostAsync(game, worldPort, hostLifetime.Token, hostKey);
    Check(RoomProtocol.IsToken(room.Code) && await service.FindAsync(room.Code, ct) == game, "The invite resolves to the host's game and modpack");
    await CheckMissingAsync(RoomProtocol.Token());
    var tunnel = service.StartGuest(room.Code, () => { }, guestLifetime.Token);
    using var firstClient = new TcpClient();
    using var secondClient = new TcpClient();
    await firstClient.ConnectAsync(IPAddress.Loopback, tunnel.Port, ct);
    using var firstWorldConnection = await world.AcceptTcpClientAsync(ct);
    await secondClient.ConnectAsync(IPAddress.Loopback, tunnel.Port, ct);
    using var secondWorldConnection = await world.AcceptTcpClientAsync(ct);
    var firstGuest = firstClient.GetStream();
    var secondGuest = secondClient.GetStream();
    var firstHost = firstWorldConnection.GetStream();
    var secondHost = secondWorldConnection.GetStream();
    await Task.WhenAll(TransferAsync(firstGuest, firstHost), TransferAsync(secondHost, secondGuest));
    await Task.WhenAll(TransferAsync(firstHost, firstGuest), TransferAsync(secondGuest, secondHost));
    Check(true, "Two simultaneous game connections passed large byte streams in both directions");

    firstClient.Dispose();
    Check(await firstHost.ReadAsync(new byte[1].AsMemory(), ct) == 0, "Closing one game connection also closes its host leg");
    await TransferAsync(secondGuest, secondHost);
    Check(true, "Another guest remains connected");

    hostLifetime.Cancel();
    await ObserveCancellationAsync(room.Completion);
    Check(await secondGuest.ReadAsync(new byte[1].AsMemory(), ct) == 0, "Closing the host room disconnects guests");
    await CheckMissingAsync(room.Code);
    guestLifetime.Cancel();
    await ObserveCancellationAsync(tunnel.Completion);
    using var disconnected = new TcpClient();
    try
    {
        await disconnected.ConnectAsync(IPAddress.Loopback, tunnel.Port, ct);
        throw new Exception("The local tunnel listener is still running.");
    }
    catch (SocketException) { Check(true, "Closing the guest session releases its local port"); }

    var unavailablePort = ((IPEndPoint)world.LocalEndpoint).Port;
    world.Stop();
    using var unavailableLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var unavailable = await service.HostAsync(game, unavailablePort, unavailableLifetime.Token, hostKey);
    var connectionFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var unavailableTunnel = service.StartGuest(unavailable.Code, () => connectionFailed.TrySetResult(), unavailableLifetime.Token);
    using var stalled = new TcpClient();
    await stalled.ConnectAsync(IPAddress.Loopback, unavailableTunnel.Port, ct);
    await connectionFailed.Task.WaitAsync(ct);
    Check(await stalled.GetStream().ReadAsync(new byte[1].AsMemory(), ct) == 0, "An unavailable world ends the guest connection and reports a failure");
    unavailableLifetime.Cancel();
    await ObserveCancellationAsync(unavailable.Completion);
    await ObserveCancellationAsync(unavailableTunnel.Completion);
    Console.WriteLine("MULTIPLAYER_VALIDATION_OK");

    async Task CheckMissingAsync(string code)
    {
        try { await service.FindAsync(code, ct); }
        catch (InvalidDataException ex) when (ex.Message.Contains("房间不存在"))
        {
            Check(true, "Invalid or closed invite is rejected");
            return;
        }
        throw new Exception("An invalid or closed invite was accepted.");
    }

    async Task TransferAsync(Stream source, Stream destination)
    {
        var expected = RandomNumberGenerator.GetBytes(160 * 1024);
        var actual = new byte[expected.Length];
        await Task.WhenAll(source.WriteAsync(expected.AsMemory(), ct).AsTask(),
            destination.ReadExactlyAsync(actual.AsMemory(), ct).AsTask());
        if (!expected.AsSpan().SequenceEqual(actual)) throw new Exception("Relay mixed or corrupted game data.");
    }
}
finally { world.Stop(); }

static async Task ObserveCancellationAsync(Task task)
{
    try { await task; }
    catch (OperationCanceledException) { }
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
