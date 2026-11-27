using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using MCL.Multiplayer;
using MCL.Services;

if (args is ["--ipv6"])
{
    await DirectValidation.RunAsync();
    return;
}

if (args is not [var relay])
    throw new ArgumentException("Run a local MCL.Relay and provide its HTTP address.");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
using var guestLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
var ct = deadline.Token;
var service = new MultiplayerClient(relay);
Check(await service.MeasureLatencyAsync(ct) >= 0, "Relay health endpoint returns a round-trip measurement");
var latency = new RoomLatencyHistory();
foreach (var sample in new double?[] { 100, 120, null, 200, 240 }) latency.Add(sample);
var summary = latency.Snapshot();
Check(summary.Current == 240 && summary.Average == 165 && summary.Jitter == 30 && summary.Missed == 1,
    "Latency history excludes failed samples and does not calculate jitter across a gap");
for (var i = 0; i < RoomLatencyHistory.Capacity; i++) latency.Add(i);
summary = latency.Snapshot();
Check(summary.Samples.Length == RoomLatencyHistory.Capacity && summary.Missed == 0 && summary.Current == 29,
    "Latency statistics are limited to the recent window");
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
    var game = new RoomGame("1.20.1", "Multiplayer QA", "1", "0.16.14", new string('A', 64));
    var worldPort = ((IPEndPoint)world.LocalEndpoint).Port;
    if (hostKey is not null)
    {
        try { await service.HostAsync(game, worldPort, ct); throw new Exception("An unauthenticated host was allowed."); }
        catch (WebSocketException) { Check(true, "A managed relay rejects hosts without its private host key"); }
    }
    var forwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var room = await service.HostAsync(game, worldPort, hostLifetime.Token, hostKey, () => forwarded.TrySetResult());
    Check(RoomProtocol.IsToken(room.Code) && await service.FindAsync(room.Code, ct) == game, "The invite resolves to the host's game and modpack");
    await CheckMissingAsync(RoomProtocol.Token());
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tunnel = service.StartGuest(room.Code, () => { }, guestLifetime.Token, () => connected.TrySetResult());
    Task discovery;
    using (var beaconReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, GuestTunnel.LanDiscoveryPort)))
    {
        discovery = tunnel.AdvertiseLanRoomAsync(guestLifetime.Token);
        var beacon = await beaconReceiver.ReceiveAsync(ct);
        var expected = FormattableString.Invariant($"[MOTD]{GuestTunnel.LanRoomName}[/MOTD][AD]{tunnel.Port}[/AD]");
        Check(IPAddress.IsLoopback(beacon.RemoteEndPoint.Address) && Encoding.UTF8.GetString(beacon.Buffer) == expected,
            "Minecraft LAN discovery receives the forwarded room name and port through loopback");
    }
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
    await Task.WhenAll(forwarded.Task, connected.Task).WaitAsync(ct);
    Check(true, "Two simultaneous game connections passed large byte streams in both directions");

    firstClient.Dispose();
    Check(await firstHost.ReadAsync(new byte[1].AsMemory(), ct) == 0, "Closing one game connection also closes its host leg");
    await TransferAsync(secondGuest, secondHost);
    Check(true, "Another guest remains connected");

    hostLifetime.Cancel();
    await ObserveCancellationAsync(room.Completion);
    Check(await secondGuest.ReadAsync(new byte[1].AsMemory(), ct) == 0, "Closing the host room disconnects guests");
    await CheckMissingAsync(room.Code);
    using (var retryLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct))
    {
        var retry = await service.HostAsync(game, worldPort, retryLifetime.Token, hostKey);
        Check(retry.Code != room.Code && await service.FindAsync(retry.Code, ct) == game, "Room can be recreated using the same running world");
        retryLifetime.Cancel();
        await ObserveCancellationAsync(retry.Completion);
    }
    guestLifetime.Cancel();
    await ObserveCancellationAsync(tunnel.Completion);
    await ObserveCancellationAsync(discovery);
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
