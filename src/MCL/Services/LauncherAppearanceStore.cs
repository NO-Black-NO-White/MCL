using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace MCL.Services;

internal sealed record LauncherAppearance(string Theme = "cherry", bool ReduceMotion = false);

internal sealed class LauncherAppearanceStore(string dataDirectory)
{
    private const long MaxImageBytes = 32 * 1024 * 1024;

    internal LauncherAppearance Load()
    {
        try
        {
            using var stream = File.OpenRead(AppearanceFile);
            if (stream.Length <= 16_384 && JsonSerializer.Deserialize<LauncherAppearance>(stream) is { } appearance)
                return appearance with { Theme = appearance.Theme == "amethyst" ? "amethyst" : "cherry" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new LauncherAppearance();
    }

    internal void Save(LauncherAppearance appearance) =>
        WriteAtomically(AppearanceFile, stream => JsonSerializer.Serialize(stream, appearance));

    private string AppearanceFile => Path.Combine(dataDirectory, "launcher-appearance.json");

    internal string CoverPath(string category, string fullPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath)).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return Path.Combine(dataDirectory, "covers", category, key + ".jpg");
    }

    internal static BitmapSource? LoadCover(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return DecodeImage(stream);
        }
        catch (Exception ex) when (IsImageError(ex)) { return null; }
    }

    internal static void ImportCover(string source, string target)
    {
        using var input = File.OpenRead(source);
        var image = DecodeImage(input);
        var encoder = new JpegBitmapEncoder { QualityLevel = 89 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        WriteAtomically(target, encoder.Save);
    }

    private static BitmapSource DecodeImage(FileStream stream)
    {
        if (stream.Length is < 1 or > MaxImageBytes) throw new InvalidDataException("请选择不超过 32 MB 的图片。");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder or BmpBitmapDecoder) || decoder.Frames.Count != 1)
            throw new InvalidDataException("请选择单张 PNG、JPG 或 BMP 图片。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth is < 1 or > 16384 || frame.PixelHeight is < 1 or > 16384
            || (long)frame.PixelWidth * frame.PixelHeight > 70_000_000)
            throw new InvalidDataException("图片分辨率过大或无效。");
        var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (longest > 1920)
        {
            if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = 1920;
            else image.DecodePixelHeight = 1920;
        }
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static bool IsImageError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidDataException or NotSupportedException or FormatException or ArgumentException
        or System.Runtime.InteropServices.COMException;

    private static void WriteAtomically(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary)) write(stream);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
