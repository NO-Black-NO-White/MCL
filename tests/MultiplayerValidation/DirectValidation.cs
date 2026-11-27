using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MCL.Multiplayer;
using MCL.Services;

internal static class DirectValidation
{
    internal static async Task RunAsync()
    {
        var token = RoomProtocol.Token();
        var pin = new string('A', 64);
        var endpoint = new IPEndPoint(IPAddress.Parse("2600:1234:5678:9abc:def0:1234:5678:9abc"), 65535);
        var encoded = RoomInvite.CreateDirect(endpoint, token, pin);
        var parsed = RoomInvite.Read(encoded, "");
        Check(encoded.Length <= 160 && parsed.Direct == new DirectRoomEndpoint(endpoint.Address, endpoint.Port, pin.ToLowerInvariant())
            && parsed.Code == token, "IPv6 invites preserve address, port, secret and certificate identity");
        foreach (var invalid in new[] { "::1", "::ffff:192.0.2.1", "fe80::1", "fd00::1", "127.0.0.1", "2001:db8::1" })
            await RejectAsync(() => Task.FromResult(RoomInvite.Read($"MCL6/{invalid}/1234/{token}/{pin}", "")));
        await RejectAsync(() => Task.FromResult(RoomInvite.Read($"MCL6/{endpoint.Address}/0/{token}/{pin}", "")));
        await RejectAsync(() => Task.FromResult(RoomInvite.Read($"MCL6/{endpoint.Address}/1234/{token}/abcd", "")));
        Check(true, "IPv6 invites reject local, reserved and malformed targets");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var guestLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var ct = deadline.Token;
        var world = new TcpListener(IPAddress.Loopback, 0);
        world.Start();
        try
        {
            var game = new RoomGame("1.21.11", "Direct QA", "1", "Fabric", new string('B', 64));
            var host = DirectMultiplayer.Host(game, IPAddress.IPv6Loopback, ((IPEndPoint)world.LocalEndpoint).Port, hostLifetime.Token);
            var fields = host.Invite.Split('/');
            var local = new DirectRoomEndpoint(IPAddress.IPv6Loopback, host.Port, fields[4]);
            var client = new DirectMultiplayer(local, fields[3]);
            Check(await client.FindAsync(ct) == game, "A direct IPv6 room returns its version and mod fingerprint");
            await RejectAsync(() => new DirectMultiplayer(local, RoomProtocol.Token()).FindAsync(ct));
            await RejectAsync(() => new DirectMultiplayer(local with { Pin = new string('0', 64) }, fields[3]).FindAsync(ct));
            Check(!world.Pending(), "Info requests and failed authentication do not connect to the game");

            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tunnel = client.StartGuest(() => { }, guestLifetime.Token, () => connected.TrySetResult());
            using var guest = new TcpClient();
            await guest.ConnectAsync(IPAddress.Loopback, tunnel.Port, ct);
            using var playing = await world.AcceptTcpClientAsync(ct);
            await connected.Task.WaitAsync(ct);
            var player = guest.GetStream();
            var gameStream = playing.GetStream();
            await Task.WhenAll(TransferAsync(player, gameStream), TransferAsync(gameStream, player));
            Check(true, "Real IPv6 TCP carries game data in both directions without a relay");
            await hostLifetime.CancelAsync();
            try { await host.Completion; }
            catch (OperationCanceledException) { }
            Check(await player.ReadAsync(new byte[1], ct) == 0, "Stopping a direct room closes the game connection");
            await RejectAsync(() => client.FindAsync(ct));
            await guestLifetime.CancelAsync();
            try { await tunnel.Completion; }
            catch (OperationCanceledException) { }
        }
        finally { world.Stop(); }
        Console.WriteLine("IPV6_VALIDATION_OK");
    }

    private static async Task TransferAsync(Stream source, Stream destination)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var expected = RandomNumberGenerator.GetBytes(80 * 1024);
        var received = new byte[expected.Length];
        await Task.WhenAll(source.WriteAsync(expected, timeout.Token).AsTask(),
            destination.ReadExactlyAsync(received, timeout.Token).AsTask());
        Check(expected.SequenceEqual(received), "Forwarded bytes are unchanged");
    }

    private static async Task RejectAsync<T>(Func<Task<T>> operation)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return; }
        throw new InvalidOperationException("An invalid direct connection was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
