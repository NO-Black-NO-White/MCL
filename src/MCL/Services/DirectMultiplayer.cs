using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MCL.Multiplayer;

namespace MCL.Services;

internal sealed record DirectHostedRoom(string Invite, int Port, Task Completion);

internal sealed class DirectMultiplayer(DirectRoomEndpoint endpoint, string code)
{
    private const byte Info = 1;
    private const byte Play = 2;
    private const byte Accepted = 1;
    private const int RequestLength = 19;
    private const int MaximumInfoBytes = 2048;
    private const string CertificateName = "mcl-direct";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);

    internal static bool IsGlobalIpv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0) return false;
        var bytes = address.GetAddressBytes();
        return (bytes[0] & 0xe0) == 0x20 && !bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x20, 0x01, 0x0d, 0xb8 });
    }

    internal static IPAddress? FindLocalAddress()
    {
        if (!Socket.OSSupportsIPv6) return null;
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(adapter => (adapter.NetworkInterfaceType, Properties: adapter.GetIPProperties()))
            .SelectMany(adapter => adapter.Properties.UnicastAddresses
                .Where(ip => IsGlobalIpv6(ip.Address) && (!OperatingSystem.IsWindows() || ip.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred))
                .Select(ip => (ip.Address, Temporary: OperatingSystem.IsWindows() && ip.SuffixOrigin == SuffixOrigin.Random,
                    adapter.NetworkInterfaceType, HasGateway: adapter.Properties.GatewayAddresses
                    .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetworkV6))))
            .OrderByDescending(candidate => candidate.HasGateway)
            .ThenByDescending(candidate => candidate.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .ThenBy(candidate => candidate.Temporary)
            .Select(candidate => candidate.Address)
            .FirstOrDefault();
    }

    internal static DirectHostedRoom Host(RoomGame game, IPAddress address, int gamePort, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(game, RoomProtocol.Json);
        if (!game.IsSupported || bytes.Length > MaximumInfoBytes || gamePort is < 1 or > 65535)
            throw new InvalidDataException("这份游戏信息无法用于 IPv6 直连。");
        var listener = new TcpListener(address, 0);
        var certificate = CreateCertificate();
        try
        {
            var tls = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(certificate, null, offline: true)
            };
            listener.Start(16);
            var token = RoomProtocol.Token();
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var bound = (IPEndPoint)listener.LocalEndpoint;
            var invite = RoomInvite.CreateDirect(bound, token, pin);
            return new(invite, bound.Port, HostAsync(listener, certificate, tls, Convert.FromHexString(token), bytes, gamePort, ct));
        }
        catch
        {
            listener.Stop();
            certificate.Dispose();
            throw;
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = new CertificateRequest($"CN={CertificateName}", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddMinutes(-5), now.AddDays(7));
        // Schannel needs an imported private key. The user key container is removed on disposal.
        var identity = certificate.Export(X509ContentType.Pfx);
        try
        {
            var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
            return X509CertificateLoader.LoadPkcs12(identity, null, flags);
        }
        finally { CryptographicOperations.ZeroMemory(identity); }
    }

    private static async Task HostAsync(TcpListener listener, X509Certificate2 certificate, SslServerAuthenticationOptions tls, byte[] token,
        byte[] info, int gamePort, CancellationToken ct)
    {
        using (certificate)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var slots = new SemaphoreSlim(RoomProtocol.MaximumGuests))
        {
            var clients = new List<Task>();
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    clients.RemoveAll(item => item.IsCompletedSuccessfully);
                    if (clients.Count < RoomProtocol.MaximumGuests * 2)
                        clients.Add(HandleGuestAsync(client, tls, token, info, gamePort, slots, lifetime.Token));
                    else client.Dispose();
                }
            }
            finally
            {
                listener.Stop();
                await lifetime.CancelAsync();
                await Task.WhenAll(clients);
            }
        }
    }

    private static async Task HandleGuestAsync(TcpClient client, SslServerAuthenticationOptions tls, byte[] token,
        byte[] info, int gamePort, SemaphoreSlim slots, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var timeout = WithDeadline(ct);
                using var stream = new SslStream(client.GetStream());
                await stream.AuthenticateAsServerAsync(tls, timeout.Token);
                var request = new byte[RequestLength];
                await stream.ReadExactlyAsync(request, timeout.Token);
                if (!CryptographicOperations.FixedTimeEquals(request.AsSpan(1), token)) return;
                if (request[0] == Info)
                {
                    var prefix = new byte[3];
                    prefix[0] = Accepted;
                    BinaryPrimitives.WriteUInt16BigEndian(prefix.AsSpan(1), (ushort)info.Length);
                    await stream.WriteAsync(prefix, timeout.Token);
                    await stream.WriteAsync(info, timeout.Token);
                }
                else if (request[0] == Play && slots.Wait(0))
                {
                    try
                    {
                        using var game = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                        await game.ConnectAsync(IPAddress.Loopback, gamePort, timeout.Token);
                        await stream.WriteAsync(new byte[] { Accepted }, timeout.Token);
                        await BridgeAsync(stream, game.GetStream(), ct);
                    }
                    finally { slots.Release(); }
                }
            }
            catch (Exception ex) when (IsConnectionError(ex)) { }
        }
    }

    internal async Task<RoomGame> FindAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = WithDeadline(ct);
            using var stream = await ConnectAsync(Info, timeout.Token);
            var header = new byte[2];
            await stream.ReadExactlyAsync(header, timeout.Token);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header);
            if (length is 0 or > MaximumInfoBytes) throw new InvalidDataException("IPv6 房间返回的游戏信息无效。");
            var data = new byte[length];
            await stream.ReadExactlyAsync(data, timeout.Token);
            return JsonSerializer.Deserialize<RoomGame>(data, RoomProtocol.Json) is { IsSupported: true } game
                ? game : throw new InvalidDataException("IPv6 房间返回的游戏信息无效。");
        }
        catch (AuthenticationException ex)
        { throw new IOException("无法验证房主身份，请让房主复制最新的 IPv6 邀请码。", ex); }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsConnectionError(ex))
        { throw new IOException("无法直连房主。请确认房间仍开放、双方 IPv6 可用，并检查房主的防火墙和校园网入站限制。", ex); }
    }

    internal GuestTunnel StartGuest(Action connectionFailed, CancellationToken ct, Action? connected = null) =>
        GuestTunnel.Start(async (game, token) =>
        {
            using var timeout = WithDeadline(token);
            using var stream = await ConnectAsync(Play, timeout.Token);
            connected?.Invoke();
            await BridgeAsync(game, stream, token);
        }, connectionFailed, ct);

    private async Task<SslStream> ConnectAsync(byte mode, CancellationToken ct)
    {
        var pin = Convert.FromHexString(endpoint.Pin);
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        SslStream? stream = null;
        try
        {
            await socket.ConnectAsync(new IPEndPoint(endpoint.Ip, endpoint.Port), ct);
            stream = new SslStream(new NetworkStream(socket, ownsSocket: true), false, (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pin));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = CertificateName }, ct);
            var request = new byte[RequestLength];
            request[0] = mode;
            Convert.FromHexString(code).CopyTo(request, 1);
            await stream.WriteAsync(request, ct);
            var reply = new byte[1];
            await stream.ReadExactlyAsync(reply, ct);
            if (reply[0] != Accepted) throw new IOException("IPv6 房间未接受连接。");
            return stream;
        }
        catch
        {
            stream?.Dispose();
            socket.Dispose();
            throw;
        }
    }

    private static async Task BridgeAsync(Stream first, Stream second, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var outbound = first.CopyToAsync(second, RoomProtocol.BufferSize, lifetime.Token);
        var inbound = second.CopyToAsync(first, RoomProtocol.BufferSize, lifetime.Token);
        await Task.WhenAny(outbound, inbound);
        await lifetime.CancelAsync();
        try { await Task.WhenAll(outbound, inbound); }
        catch (Exception ex) when (IsConnectionError(ex)) { }
    }

    private static CancellationTokenSource WithDeadline(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        return timeout;
    }

    private static bool IsConnectionError(Exception ex) =>
        ex is IOException or SocketException or AuthenticationException or OperationCanceledException;
}
