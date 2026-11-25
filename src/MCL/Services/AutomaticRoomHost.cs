using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using MCL.Multiplayer;

namespace MCL.Services;

internal sealed record HostedRelay(Uri Relay, Uri Public, string Key, bool PublicAddressVerified,
    Task<Process> Completion, RoomProcessGroup Processes) : IDisposable
{
    public void Dispose() => Processes.Dispose();
}

internal sealed partial class AutomaticRoomHost(string dataDirectory)
{
    private const string CloudflareVersion = "2026.11.2";
    private const int CloudflareSize = 55_470_800;
    private const string CloudflareSha256 = "555b46c85582461d189cac30d14ad05480d612613801be9f110b8ce08c5b2113";
    private static readonly HttpClient LoopbackHttp = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    internal async Task<HostedRelay> StartAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var cache = ModpackPaths.SafeTarget(dataDirectory, "multiplayer");
        Directory.CreateDirectory(cache);
        using var publicHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(12) })
        { Timeout = Timeout.InfiniteTimeSpan };
        var relayFile = await FindRelayAsync(cache, ct).ConfigureAwait(false);
        var tunnelFile = await FindTunnelAsync(cache, publicHttp, progress, ct).ConfigureAwait(false);
        var group = new RoomProcessGroup();
        try
        {
            progress?.Report("正在启动本机中转…");
            var key = RoomProtocol.Token();
            var relayStart = StartInfo(relayFile, cache, "--urls", "http://127.0.0.1:0");
            relayStart.Environment["MCL_RelayHostKey"] = key;
            var relay = group.Start(relayStart);
            var ready = await WaitForLineAsync(relay, ParseRelay, "本机中转", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            var local = new Uri(ready[0]);
            await WaitForHealthAsync(LoopbackHttp, local, ready[1], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

            progress?.Report("正在建立公网隧道…");
            var tunnelDiagnostics = new TunnelStartupDiagnostics();
            var tunnel = group.Start(StartInfo(tunnelFile, cache, "tunnel", "--url", local.GetLeftPart(UriPartial.Authority),
                "--protocol", "http2", "--loglevel", "info", "--metrics", "127.0.0.1:0"));
            Uri publicAddress;
            bool publicAddressVerified;
            try
            {
                publicAddress = await WaitForLineAsync(tunnel, ParseTunnel, "Cloudflare 公网隧道", TimeSpan.FromSeconds(50),
                    ct, tunnelDiagnostics.Observe).ConfigureAwait(false);
                publicAddressVerified = await VerifyPublicAddressAsync(publicHttp, publicAddress, ready[1],
                    tunnelDiagnostics, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (tunnelDiagnostics.Latest is not null)
            {
                throw new IOException($"{ex.Message} 隧道诊断：{tunnelDiagnostics.Latest}。", ex);
            }
            return new(local, publicAddress, key, publicAddressVerified, FirstExitAsync(relay, tunnel), group);
        }
        catch
        {
            group.Dispose();
            throw;
        }
    }

    private static ProcessStartInfo StartInfo(string executable, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<Process> FirstExitAsync(Process first, Process second)
    {
        var firstExit = first.WaitForExitAsync();
        var secondExit = second.WaitForExitAsync();
        var ended = await Task.WhenAny(firstExit, secondExit).ConfigureAwait(false);
        return ReferenceEquals(ended, firstExit) ? first : second;
    }

    private static async Task<T> WaitForLineAsync<T>(Process process, Func<string, T?> parse,
        string component, TimeSpan wait, CancellationToken ct, Action<string>? observe = null) where T : class
    {
        var signal = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = DrainAsync(process.StandardOutput);
        var errors = DrainAsync(process.StandardError);
        var exit = process.WaitForExitAsync();
        try
        {
            await Task.WhenAny(signal.Task, exit).WaitAsync(wait, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (process.HasExited || !signal.Task.IsCompletedSuccessfully)
            {
                // A short-lived child can exit before its final stderr line has been read.
                try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (TimeoutException) { }
                throw new IOException($"{component}在就绪前退出（退出码 {process.ExitCode}）。可在当前存档重试创建房间。");
            }
            return await signal.Task.ConfigureAwait(false);
        }
        catch (TimeoutException ex) { throw new IOException($"{component}启动超时。", ex); }

        async Task DrainAsync(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    observe?.Invoke(line);
                    if (!signal.Task.IsCompleted && parse(line) is { } result) signal.TrySetResult(result);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    private static string[]? ParseRelay(string line)
    {
        if (!line.StartsWith(RoomProtocol.ReadyPrefix, StringComparison.Ordinal)
            || line[RoomProtocol.ReadyPrefix.Length..].Split(' ') is not [var address, var id]
            || !RoomProtocol.IsToken(id) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.IsDefaultPort || uri.AbsolutePath != "/"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        return [address, id];
    }

    private static Uri? ParseTunnel(string line)
    {
        var match = TunnelAddress().Match(line);
        return match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            && RoomInvite.IsTunnelHost(uri.Host) && uri.Host != "api.trycloudflare.com" ? uri : null;
    }

    private sealed class TunnelStartupDiagnostics
    {
        private string? _latest;
        private int _metricsPort;
        internal string? Latest => Volatile.Read(ref _latest);
        internal int MetricsPort => Volatile.Read(ref _metricsPort);

        internal void Observe(string line)
        {
            var metrics = MetricsAddress().Match(line);
            if (metrics.Success && int.TryParse(metrics.Groups[1].ValueSpan, out var port) && port is > 0 and <= 65535)
                Volatile.Write(ref _metricsPort, port);

            // Cloudflared output can contain connection addresses and configuration. Keep only known error categories.
            var status = ProvisioningStatus().Match(line);
            if (status.Success)
            {
                var code = status.Groups[1].Value;
                var detail = code == "429" ? "Cloudflare 限制了临时隧道申请频率（HTTP 429）"
                    : $"Cloudflare 拒绝或未能处理临时隧道申请（HTTP {code}）";
                Volatile.Write(ref _latest, detail);
                return;
            }

            var related = line.Contains("failed to request quick tunnel", StringComparison.OrdinalIgnoreCase)
                || line.Contains("quick tunnel provisioning failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("edge discovery", StringComparison.OrdinalIgnoreCase)
                || line.Contains("dial edge", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed to dial", StringComparison.OrdinalIgnoreCase)
                || line.Contains("pre-check", StringComparison.OrdinalIgnoreCase);
            if (!related) return;

            var error = line.ToLowerInvariant();
            var reason = error switch
            {
                var value when value.Contains("lookup ") || value.Contains("dns") || value.Contains("no such host") => "无法解析 Cloudflare 隧道相关域名",
                var value when value.Contains("certificate") || value.Contains("x509") => "连接 Cloudflare 时证书验证失败",
                var value when value.Contains("timeout") || value.Contains("deadline exceeded") => "连接 Cloudflare 超时",
                var value when value.Contains("tls") => "与 Cloudflare 建立加密连接失败",
                var value when value.Contains("refused") => "连接 Cloudflare 时对端拒绝了连接",
                var value when value.Contains("reset") || value.Contains("forcibly closed") => "连接 Cloudflare 时连接被重置",
                var value when value.Contains("quick tunnel provisioning failed") => "Cloudflare 未能分配临时隧道",
                var value when value.Contains("failed to request quick tunnel") => "未能向 Cloudflare 提交临时隧道申请",
                _ => null
            };
            if (reason is not null) Volatile.Write(ref _latest, reason);
        }
    }

    private static async Task<bool> VerifyPublicAddressAsync(HttpClient client, Uri address, string relayId,
        TunnelStartupDiagnostics diagnostics, TimeSpan limit, CancellationToken ct)
    {
        try
        {
            await WaitForHealthAsync(client, address, relayId, limit, ct).ConfigureAwait(false);
            return true;
        }
        catch (RelayHealthException ex) when (ex.NameResolutionFailed)
        {
            // The host may resolve the public hostname differently from its guests. Fall back only
            // when this cloudflared process confirms that its connection to Cloudflare is active.
            if (diagnostics.MetricsPort is > 0 and var port
                && await IsTunnelRegisteredAsync(port, address, ct).ConfigureAwait(false)) return false;
            throw;
        }
    }

    private static async Task<bool> IsTunnelRegisteredAsync(int port, Uri address, CancellationToken ct)
    {
        var metrics = new Uri($"http://127.0.0.1:{port}/");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var identity = await LoopbackHttp.GetAsync(new Uri(metrics, "quicktunnel"),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (identity.StatusCode != HttpStatusCode.OK) return false;
            await using var stream = await identity.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[512];
            var count = await stream.ReadAtLeastAsync(buffer.AsMemory(), buffer.Length, false, timeout.Token).ConfigureAwait(false);
            if (count == buffer.Length) return false;
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("hostname", out var host)
                || host.ValueKind != JsonValueKind.String) return false;
            var name = host.GetString();
            if (!string.Equals(name, address.Host, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, address.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) return false;

            using var ready = await LoopbackHttp.GetAsync(new Uri(metrics, "ready"),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return ready.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class RelayHealthException(string message, Exception inner, bool nameResolutionFailed) : IOException(message, inner)
    {
        internal bool NameResolutionFailed { get; } = nameResolutionFailed;
    }

    private static async Task WaitForHealthAsync(HttpClient client, Uri address, string relayId, TimeSpan limit, CancellationToken ct)
    {
        var lastResult = "等待响应超时";
        var nameResolutionFailed = false;
        var receivedResponse = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            while (true)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    using var reply = await client.GetAsync(new Uri(address, $"{RoomProtocol.Prefix}/health"),
                        HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                    if (reply.StatusCode == HttpStatusCode.OK && reply.Headers.TryGetValues(RoomProtocol.RelayIdHeader, out var values)
                        && values.Contains(relayId, StringComparer.Ordinal)) return;
                    receivedResponse = true;
                    nameResolutionFailed = false;
                    lastResult = reply.StatusCode == HttpStatusCode.OK ? "响应来自其他服务" : $"HTTP {(int)reply.StatusCode}";
                }
                catch (HttpRequestException ex)
                {
                    nameResolutionFailed = ex.HttpRequestError == HttpRequestError.NameResolutionError;
                    lastResult = ex.HttpRequestError switch
                    {
                        HttpRequestError.NameResolutionError => "无法解析隧道地址",
                        HttpRequestError.SecureConnectionError => "TLS 连接失败",
                        HttpRequestError.ProxyTunnelError => "系统代理连接失败",
                        _ => "网络连接失败"
                    };
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
                {
                    nameResolutionFailed = false;
                    lastResult = "等待响应超时";
                }
                await Task.Delay(650, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            var target = address.IsLoopback ? "本机中转" : "公网隧道";
            throw new RelayHealthException($"{target}未通过连通性检查（{lastResult}）。请重试。", ex,
                nameResolutionFailed && !receivedResponse);
        }
    }

    private static async Task<string> FindRelayAsync(string cache, CancellationToken ct)
    {
        var resource = Application.GetResourceStream(new Uri("/MCL;component/Assets/relay.exe", UriKind.Relative));
        await using var stream = resource?.Stream
            ?? throw new IOException("当前是开发构建，缺少随正式版发布的本机中转程序。请使用打包好的 MCL.exe。");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        var path = ModpackPaths.SafeTarget(cache, $"relay/{hash}/MCL.Relay.exe");
        if (!await MatchesAsync(path, stream.Length, hash, ct).ConfigureAwait(false))
        {
            stream.Position = 0;
            await SaveAsync(stream, path, stream.Length, hash, ct).ConfigureAwait(false);
        }
        return path;
    }

    private static async Task<string> FindTunnelAsync(string cache, HttpClient client, IProgress<string>? progress, CancellationToken ct)
    {
        var target = ModpackPaths.SafeTarget(cache, $"cloudflared/{CloudflareVersion}/cloudflared.exe");
        if (await MatchesAsync(target, CloudflareSize, CloudflareSha256, ct).ConfigureAwait(false)) return target;
        foreach (var location in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var installed = Path.Combine(Environment.GetFolderPath(location), "cloudflared", "cloudflared.exe");
            if (!await MatchesAsync(installed, CloudflareSize, CloudflareSha256, ct).ConfigureAwait(false)) continue;
            await using var input = File.OpenRead(installed);
            await SaveAsync(input, target, CloudflareSize, CloudflareSha256, ct).ConfigureAwait(false);
            return target;
        }

        progress?.Report("首次开房，正在下载并校验 Cloudflare 隧道组件…");
        using var download = CancellationTokenSource.CreateLinkedTokenSource(ct);
        download.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var address = new Uri($"https://github.com/cloudflare/cloudflared/releases/download/{CloudflareVersion}/cloudflared-windows-amd64.exe");
            for (var hops = 0; hops < 4; hops++)
            {
                if (address.Scheme != "https" || !address.IsDefaultPort || address.UserInfo.Length != 0
                    || address.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new IOException("隧道组件下载跳转到了未知地址。");
                using var reply = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, download.Token).ConfigureAwait(false);
                if ((int)reply.StatusCode is >= 300 and < 400 && reply.Headers.Location is { } redirect)
                { address = new Uri(address, redirect); continue; }
                reply.EnsureSuccessStatusCode();
                await using var stream = await reply.Content.ReadAsStreamAsync(download.Token).ConfigureAwait(false);
                await SaveAsync(stream, target, CloudflareSize, CloudflareSha256, download.Token).ConfigureAwait(false);
                return target;
            }
            throw new IOException("隧道组件下载的跳转次数过多。");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        { throw new IOException("无法从 Cloudflare 的 GitHub 发布页下载隧道组件。请检查网络后重试。", ex); }
    }

    private static async Task<bool> MatchesAsync(string path, long size, string hash, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SaveAsync(Stream input, string target, long size, string hash, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var stage = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long bytes = 0;
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    bytes += count;
                    if (bytes > size) throw new IOException("联机组件的文件大小与发布记录不符。");
                    digest.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                }
                if (bytes != size || !Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("联机组件校验失败，请重新开房重试。");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(stage, target, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    [GeneratedRegex(@"https://[a-z0-9][a-z0-9-]{0,62}\.trycloudflare\.com(?![a-z0-9.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TunnelAddress();

    [GeneratedRegex(@"quick tunnel provisioning failed with status ([45][0-9]{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProvisioningStatus();

    [GeneratedRegex(@"Starting metrics server on 127\.0\.0\.1:([0-9]{1,5})/metrics\b", RegexOptions.CultureInvariant)]
    private static partial Regex MetricsAddress();
}
