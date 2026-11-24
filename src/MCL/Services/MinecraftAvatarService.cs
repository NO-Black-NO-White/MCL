using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MCL.Services;

public sealed class MinecraftAvatarService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    public MinecraftAvatarService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<BitmapSource?> LoadAsync(string? skinUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(skinUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, "^/texture/[a-fA-F0-9]{32,128}$")) return null;
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1024 * 1024) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > 1024 * 1024) return null;
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        var skin = new BitmapImage();
        try
        {
            skin.BeginInit(); skin.CacheOption = BitmapCacheOption.OnLoad; skin.DecodePixelWidth = 64; skin.StreamSource = buffer; skin.EndInit(); skin.Freeze();
        }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        { return null; }
        if (skin.PixelWidth != 64 || skin.PixelHeight is not (32 or 64)) return null;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8)), new Rect(0, 0, 8, 8));
            drawing.DrawImage(new CroppedBitmap(skin, new Int32Rect(40, 8, 8, 8)), new Rect(0, 0, 8, 8));
        }
        var avatar = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
        avatar.Render(visual); avatar.Freeze();
        return avatar;
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
