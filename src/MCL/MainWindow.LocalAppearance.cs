using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private readonly LocalAppearanceService _localAppearance;
    private bool _applyLocalAppearance;
    private bool _useSlimSkin;
    private string? _localSkin;
    private string? _localCape;
    private BitmapSource? _localAvatar;
    private BitmapSource? _localSkinTexture;
    private BitmapSource? _localSkinPreview;
    private BitmapSource? _localCapePreview;
    private string _localAppearanceStatus = "";

    public bool ApplyLocalAppearance
    {
        get => _applyLocalAppearance;
        set { if (Set(ref _applyLocalAppearance, value)) MarkDirty(); }
    }

    public bool UseSlimSkin
    {
        get => _useSlimSkin;
        set
        {
            if (!Set(ref _useSlimSkin, value)) return;
            UpdateSkinPreview();
            MarkDirty();
        }
    }

    public BitmapSource? LocalAvatar { get => _localAvatar; private set => Set(ref _localAvatar, value); }
    public bool CanEditLocalAppearance => !IsBusy && !IsGameRunning;
    public BitmapSource? LocalSkinPreview { get => _localSkinPreview; private set => Set(ref _localSkinPreview, value); }
    public BitmapSource? LocalCapePreview { get => _localCapePreview; private set => Set(ref _localCapePreview, value); }
    public bool HasLocalSkin => _localSkin is not null;
    public bool HasLocalCape => _localCape is not null;
    public string LocalSkinLabel => HasLocalSkin ? "已选皮肤" : "默认皮肤";
    public string LocalCapeLabel => HasLocalCape ? "已选披风" : "无自定义披风";
    public string LocalAppearanceStatus { get => _localAppearanceStatus; private set => Set(ref _localAppearanceStatus, value); }

    private void LoadLocalAppearance(LauncherSettings settings)
    {
        _applyLocalAppearance = settings.ApplyLocalAppearance;
        _useSlimSkin = LocalAppearanceService.NormalizeModel(settings.LocalSkinModel) == "slim";
        _localSkin = settings.LocalSkin;
        _localCape = settings.LocalCape;
        LoadLocalTexturePreview(_localSkin, cape: false);
        LoadLocalTexturePreview(_localCape, cape: true);
    }

    private void LoadLocalTexturePreview(string? hash, bool cape)
    {
        try
        {
            var texture = _localAppearance.PreviewTexture(hash, cape);
            if (cape) SetCapePreview(texture);
            else SetSkinTexture(texture);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or
            UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            LocalAppearanceStatus = "已保存的图片无法预览，请重新选择或清空外观。";
        }
    }

    private void SetSkinTexture(BitmapSource? skin)
    {
        _localSkinTexture = skin;
        LocalAvatar = skin is null ? null : CreateLocalAvatar(skin);
        UpdateSkinPreview();
    }

    private void UpdateSkinPreview() =>
        LocalSkinPreview = _localSkinTexture is { } skin && LocalAvatar is { } avatar
            ? CreateSkinPreview(skin, avatar, UseSlimSkin)
            : null;

    private static BitmapSource CreateSkinPreview(BitmapSource skin, BitmapSource avatar, bool slim)
    {
        var modern = skin.PixelHeight == 64;
        var arm = slim ? 3 : 4;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(avatar, new Rect(4, 0, 8, 8));
            DrawPart(new(20, 20, 8, 12), 4, 8, new(20, 36, 8, 12));
            DrawPart(new(44, 20, arm, 12), 4 - arm, 8, new(44, 36, arm, 12));
            DrawPart(new(4, 20, 4, 12), 4, 20, new(4, 36, 4, 12));
            if (modern)
            {
                DrawPart(new(36, 52, arm, 12), 12, 8, new(52, 52, arm, 12));
                DrawPart(new(20, 52, 4, 12), 8, 20, new(4, 52, 4, 12));
            }
            else
            {
                DrawPart(new(44, 20, arm, 12), 12, 8, mirror: true);
                DrawPart(new(4, 20, 4, 12), 8, 20, mirror: true);
            }

            void DrawPart(Int32Rect source, int x, int y, Int32Rect? overlay = null, bool mirror = false)
            {
                var destination = new Rect(x, y, source.Width, source.Height);
                if (mirror) drawing.PushTransform(new ScaleTransform(-1, 1, x + source.Width / 2.0, y));
                drawing.DrawImage(new CroppedBitmap(skin, source), destination);
                if (modern && overlay is { } outer) drawing.DrawImage(new CroppedBitmap(skin, outer), destination);
                if (mirror) drawing.Pop();
            }
        }
        var preview = new RenderTargetBitmap(16, 32, 96, 96, PixelFormats.Pbgra32);
        preview.Render(visual);
        preview.Freeze();
        return preview;
    }

    private void SetCapePreview(BitmapSource? cape)
    {
        if (cape is null) { LocalCapePreview = null; return; }
        var front = new CroppedBitmap(cape, new Int32Rect(1, 1, 10, 16));
        front.Freeze();
        LocalCapePreview = front;
    }

    private static BitmapSource CreateLocalAvatar(BitmapSource skin)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8)), new Rect(0, 0, 8, 8));
            drawing.DrawImage(new CroppedBitmap(skin, new Int32Rect(40, 8, 8, 8)), new Rect(0, 0, 8, 8));
        }
        var avatar = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
        avatar.Render(visual);
        avatar.Freeze();
        return avatar;
    }

    public void OpenAppearance()
    {
        AppearanceNav.IsChecked = true;
        Navigate("Appearance");
    }

    private void PickLocalSkin_Click(object sender, RoutedEventArgs e) => PickLocalTexture(cape: false);
    private void PickLocalCape_Click(object sender, RoutedEventArgs e) => PickLocalTexture(cape: true);

    private void PickLocalTexture(bool cape)
    {
        if (!CanEditLocalAppearance) return;
        var dialog = new OpenFileDialog
        {
            Title = cape ? "选择本地披风" : "选择本地皮肤",
            Filter = "PNG 图片 (*.png)|*.png",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ApplyLocalTexture(_localAppearance.ImportTexture(dialog.FileName, cape), cape);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { LocalAppearanceStatus = ex.Message; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        { LocalAppearanceStatus = "图片无法读取，请重新选择有效的 PNG。"; }
    }

    private void ApplyLocalTexture((string Hash, BitmapSource Preview) texture, bool cape)
    {
        if (cape)
        {
            SetCapePreview(texture.Preview);
            _localCape = texture.Hash;
        }
        else
        {
            SetSkinTexture(texture.Preview);
            _localSkin = texture.Hash;
        }
        ApplyLocalAppearance = true;
        NotifyLocalTextures();
    }

    private void ClearLocalSkin_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditLocalAppearance) return;
        _localSkin = null;
        SetSkinTexture(null);
        NotifyLocalTextures();
    }

    private void ClearLocalCape_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditLocalAppearance) return;
        _localCape = null;
        LocalCapePreview = null;
        NotifyLocalTextures();
    }

    private void NotifyLocalTextures()
    {
        Notify(nameof(HasLocalSkin), nameof(HasLocalCape), nameof(LocalSkinLabel), nameof(LocalCapeLabel));
        MarkDirty();
        LocalAppearanceStatus = "已修改外观。点击「保存本地角色」应用。";
    }

    private async void SaveLocalAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditLocalAppearance) return;
        await RunBusyAsync(() =>
        {
            SaveCurrentSettings();
            LocalAppearanceStatus = "本地档案已保存，下次启动游戏时使用。";
            return Task.CompletedTask;
        });
    }
}
