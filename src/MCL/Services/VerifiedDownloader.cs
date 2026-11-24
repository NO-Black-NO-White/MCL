using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace MCL.Services;

internal sealed class VerifiedDownloader(HttpClient client)
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "piston-meta.mojang.com", "launchermeta.mojang.com", "piston-data.mojang.com", "launcher.mojang.com",
        "libraries.minecraft.net", "resources.download.minecraft.net"
    };

    internal static Uri CheckedUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !Hosts.Contains(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new InvalidDataException("下载清单包含不支持的文件地址。");
        if (uri.Scheme == "http") uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        if (uri.Scheme != "https") throw new InvalidDataException("下载文件必须使用 HTTPS 官方地址。");
        return uri;
    }

    internal static bool ValidSha1(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    internal async Task<byte[]> GetBytesAsync(Uri uri, int maximumBytes, string? sha1, CancellationToken ct)
    {
        return await RetryAsync(async token =>
        {
            using var response = await client.GetAsync(CheckedUri(uri.AbsoluteUri), HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("下载清单超过允许的大小。");
            using var output = new MemoryStream();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[65536];
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                if (output.Length + read > maximumBytes) throw new InvalidDataException("下载清单超过允许的大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            var bytes = output.ToArray();
            if (sha1 is not null && !string.Equals(Convert.ToHexString(SHA1.HashData(bytes)), sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载清单校验失败，已拒绝使用。");
            return bytes;
        }, ct);
    }

    internal static async Task<bool> IsValidAsync(string path, string sha1, long size, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA1.HashDataAsync(stream, ct);
        return string.Equals(Convert.ToHexString(digest), sha1, StringComparison.OrdinalIgnoreCase);
    }

    internal async Task DownloadAsync(Models.DownloadFile file, Action<int> received, CancellationToken ct, Action<long>? fileProgress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        await RetryAsync(async token =>
        {
            var temporaryPath = file.Path + "." + Guid.NewGuid().ToString("N") + ".mclpart";
            try
            {
                fileProgress?.Invoke(0);
                using var response = await client.GetAsync(CheckedUri(file.Url.AbsoluteUri), HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != file.Size)
                    throw new InvalidDataException("服务器返回的文件大小与清单不一致。");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                long downloaded = 0;
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    var buffer = new byte[65536];
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        downloaded += read;
                        if (downloaded > file.Size) throw new InvalidDataException("下载文件超过清单记录的大小。");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        received(read);
                        fileProgress?.Invoke(downloaded);
                    }
                    await output.FlushAsync(token);
                    output.Flush(true);
                }
                if (downloaded != file.Size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载文件校验失败，将重新尝试。");
                token.ThrowIfCancellationRequested();
                File.Move(temporaryPath, file.Path, overwrite: true);
                return true;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }, ct);
    }

    private static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(8));
            try { return await action(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 2) { }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new IOException("下载连接超时，请检查网络后重新尝试。", ex); }
            catch (HttpRequestException ex) when (attempt < 2 && (ex.StatusCode is null || ex.StatusCode == HttpStatusCode.TooManyRequests || (int)ex.StatusCode >= 500)) { }
            catch (InvalidDataException) when (attempt < 2) { }
            if (attempt >= 2) throw new IOException("下载连接超时，请检查网络后重新尝试。");
            await Task.Delay(TimeSpan.FromMilliseconds(350 * (attempt + 1)), ct);
        }
    }
}
