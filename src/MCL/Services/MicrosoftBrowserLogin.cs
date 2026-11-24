using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MCL.Services;

public interface IMicrosoftBrowserLogin
{
    Task<MicrosoftAuthorizationCode> AuthorizeAsync(string clientId, CancellationToken cancellationToken = default);
}

public sealed class MicrosoftAuthorizationCode
{
    public required string Code { get; init; }
    public required string CodeVerifier { get; init; }
    public required string RedirectUri { get; init; }
    public override string ToString() => "MicrosoftAuthorizationCode（授权信息已隐藏）";
}

/// <summary>System-browser PKCE flow. No passwords, cookies or page content enter MCL.</summary>
public sealed class MicrosoftBrowserLogin : IMicrosoftBrowserLogin
{
    private readonly Func<Uri, CancellationToken, Task> _openBrowser;
    private readonly TimeSpan _timeout;
    public MicrosoftBrowserLogin(Func<Uri, CancellationToken, Task>? openBrowser = null, TimeSpan? timeout = null)
    {
        _openBrowser = openBrowser ?? OpenBrowserAsync;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<MicrosoftAuthorizationCode> AuthorizeAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (!Models.MicrosoftAccountSession.IsValidClientId(clientId))
            throw new AccountAuthenticationException("请先在账号页面配置本启动器的微软应用 ID。");
        cancellationToken.ThrowIfCancellationRequested();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        TcpListener? ipv6Listener = null;
        Task<TcpClient>[]? pending = null;
        try
        {
            listener.Start(8);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var listeners = new List<TcpListener> { listener };
            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    ipv6Listener = new TcpListener(IPAddress.IPv6Loopback, port);
                    ipv6Listener.Server.DualMode = false;
                    ipv6Listener.Start(8);
                    listeners.Add(ipv6Listener);
                }
                catch (SocketException) { ipv6Listener?.Stop(); ipv6Listener = null; }
            }
            pending = listeners.Select(item => item.AcceptTcpClientAsync(deadline.Token).AsTask()).ToArray();
            var redirect = $"http://localhost:{port}/";
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(), ["response_type"] = "code", ["redirect_uri"] = redirect,
                ["response_mode"] = "query", ["scope"] = MicrosoftAccountService.Scopes,
                ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
                ["prompt"] = "select_account"
            };
            var authorize = new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?" +
                string.Join("&", parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
            await _openBrowser(authorize, deadline.Token).ConfigureAwait(false);
            while (true)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(deadline.Token).ConfigureAwait(false);
                var index = Array.IndexOf(pending, completed);
                using var connection = await completed.ConfigureAwait(false);
                pending[index] = listeners[index].AcceptTcpClientAsync(deadline.Token).AsTask();
                if (connection.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address)) continue;
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    using var stream = connection.GetStream();
                    var header = await ReadHeadersAsync(stream, requestTimeout.Token).ConfigureAwait(false);
                    if (!TryCallback(header, port, out var values))
                    { await RespondAsync(stream, false, requestTimeout.Token).ConfigureAwait(false); continue; }
                    if (!values.TryGetValue("state", out var receivedState) || !StateMatches(state, receivedState))
                    { await RespondAsync(stream, false, requestTimeout.Token).ConfigureAwait(false); continue; }
                    if (values.ContainsKey("error"))
                    {
                        await RespondAsync(stream, false, requestTimeout.Token).ConfigureAwait(false);
                        throw new AccountAuthenticationException(values["error"] == "access_denied"
                            ? "微软授权已取消。可以返回启动器重新登录。" : "微软未完成授权，请返回启动器重试或检查应用配置。", requiresLogin: true);
                    }
                    if (!values.TryGetValue("code", out var code) || code.Length is < 1 or > 16_384 || code.Any(char.IsControl))
                    { await RespondAsync(stream, false, requestTimeout.Token).ConfigureAwait(false); continue; }
                    await RespondAsync(stream, true, requestTimeout.Token).ConfigureAwait(false);
                    return new MicrosoftAuthorizationCode { Code = code, CodeVerifier = verifier, RedirectUri = redirect };
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { /* Drop an idle local request. */ }
                catch (IOException) when (!deadline.IsCancellationRequested) { /* Browser closed this connection. */ }
                catch (InvalidDataException) { /* Ignore malformed local traffic until the real callback arrives. */ }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AccountAuthenticationException("等待微软登录超时，请重新点击登录。", requiresLogin: true); }
        catch (SocketException)
        { throw new AccountAuthenticationException("无法建立本机登录回调，请检查安全软件是否阻止 MCL，再重试。"); }
        finally
        {
            deadline.Cancel();
            listener.Stop();
            ipv6Listener?.Stop();
            if (pending is not null)
                foreach (var task in pending)
                    _ = task.ContinueWith(static completed =>
                    {
                        if (completed.IsCompletedSuccessfully) completed.Result.Dispose();
                        else _ = completed.Exception;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static bool StateMatches(string expected, string actual) => actual.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16_384];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("Incomplete callback.");
            count += read;
            if (count >= 4 && buffer[count - 4] == 13 && buffer[count - 3] == 10 && buffer[count - 2] == 13 && buffer[count - 1] == 10)
                return Encoding.ASCII.GetString(buffer, 0, count);
        }
        throw new InvalidDataException("Callback too large.");
    }

    private static bool TryCallback(string header, int port, out Dictionary<string, string> values)
    {
        values = new(StringComparer.Ordinal);
        var lines = header.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[0] != "GET" || first[2] is not ("HTTP/1.0" or "HTTP/1.1") || !first[1].StartsWith("/?", StringComparison.Ordinal)) return false;
        var hosts = lines.Skip(1).Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hosts.Length != 1 || !hosts[0][5..].Trim().Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)) return false;
        var query = first[1][2..];
        try
        {
            foreach (var part in query.Split('&'))
            {
                var split = part.IndexOf('=');
                if (split < 1) return false;
                var key = Uri.UnescapeDataString(part[..split].Replace('+', ' '));
                var value = Uri.UnescapeDataString(part[(split + 1)..].Replace('+', ' '));
                if (!values.TryAdd(key, value)) return false;
            }
        }
        catch (UriFormatException) { return false; }
        return true;
    }

    private static async Task RespondAsync(NetworkStream stream, bool success, CancellationToken ct)
    {
        var message = success ? "微软授权已收到" : "此次请求未完成授权";
        var detail = success ? "请返回 MCL，启动器正在验证游戏资格。你可以关闭此页面。" : "请返回 MCL，并在需要时重新登录。";
        var body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>MCL 登录</title><body><h1>" + message + "</h1><p>" + detail + "</p></body></html>");
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nPragma: no-cache\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Task OpenBrowserAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            // Shell activation may reuse an existing browser and return no process handle.
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new AccountAuthenticationException("无法打开系统浏览器，请设置默认浏览器后重试。"); }
        return Task.CompletedTask;
    }
}
