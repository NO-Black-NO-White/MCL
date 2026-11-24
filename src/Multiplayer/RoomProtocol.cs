using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCL.Multiplayer;

internal sealed record RoomGame(string Minecraft, string? Pack, string? PackVersion, string? Loader)
{
    internal bool IsSupported => Minecraft is { Length: <= 24 } && Version.TryParse(Minecraft, out var version)
        && version >= new Version(1, 20) && Label(Pack, 120) && Label(PackVersion, 120) && Label(Loader, 32);

    private static bool Label(string? value, int length) => value is null || (value.Length <= length && !value.Any(char.IsControl));
}

internal static class RoomProtocol
{
    internal const string Prefix = "/mcl/v1";
    internal const int BufferSize = 32 * 1024;
    internal const int MaximumGuests = 4;
    internal static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
    internal static bool IsToken(string? value) => value?.Length == 36 && value.All(Uri.IsHexDigit);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static ValueTask SendTextAsync(WebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, ct);

    internal static async Task<string?> ReadTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[2048];
        var length = 0;
        while (length < buffer.Length)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            if (frame.MessageType != WebSocketMessageType.Text) break;
            length += frame.Count;
            if (frame.EndOfMessage) return new UTF8Encoding(false, true).GetString(buffer, 0, length);
        }
        throw new InvalidDataException("无效的联机服务消息。");
    }

    internal static async Task BridgeAsync(WebSocket first, WebSocket second, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await FinishTogetherAsync(ForwardAsync(first, second, lifetime.Token),
            ForwardAsync(second, first, lifetime.Token), lifetime, first, second);
    }

    internal static async Task BridgeAsync(WebSocket socket, Stream stream, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await FinishTogetherAsync(ReceiveAsync(socket, stream, lifetime.Token),
            SendAsync(stream, socket, lifetime.Token), lifetime, socket);
    }

    private static async Task FinishTogetherAsync(Task first, Task second, CancellationTokenSource lifetime,
        WebSocket socket, WebSocket? peer = null)
    {
        await Task.WhenAny(first, second);
        await lifetime.CancelAsync();
        socket.Abort();
        peer?.Abort();
        try { await Task.WhenAll(first, second); }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException) { }
    }

    private static async Task ForwardAsync(WebSocket source, WebSocket destination, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var frame = await source.ReceiveAsync(buffer.AsMemory(), ct);
            if (frame.MessageType == WebSocketMessageType.Close) return;
            if (frame.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("无效的游戏连接数据。");
            // The tunnel transports a TCP byte stream, so WebSocket message boundaries have no meaning.
            if (frame.Count > 0)
                await destination.SendAsync(buffer.AsMemory(0, frame.Count), WebSocketMessageType.Binary, true, ct);
        }
    }

    private static async Task ReceiveAsync(WebSocket source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var frame = await source.ReceiveAsync(buffer.AsMemory(), ct);
            if (frame.MessageType == WebSocketMessageType.Close) return;
            if (frame.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("无效的游戏连接数据。");
            if (frame.Count > 0) await destination.WriteAsync(buffer.AsMemory(0, frame.Count), ct);
        }
    }

    private static async Task SendAsync(Stream source, WebSocket destination, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
            await destination.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, ct);
    }
}
