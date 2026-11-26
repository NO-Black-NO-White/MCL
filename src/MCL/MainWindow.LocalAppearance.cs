using System.Windows;
using System.Windows.Input;
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
    private BitmapSource? _localCapeTexture;
    private Point _appearanceDragPoint;
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
            SetPublicPreview(false);
            UpdateCharacterPreview();
            MarkDirty();
        }
    }

    public BitmapSource? LocalAvatar { get => _localAvatar; private set => Set(ref _localAvatar, value); }
    public bool CanEditLocalAppearance => !IsBusy && !IsGameRunning;
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
            if (cape) _localCapeTexture = texture;
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
    }

    private void UpdateCharacterPreview() =>
        CharacterVisual.Content = _previewingPublicAppearance && _publicAppearance is { } preview
            ? CharacterPreviewModel.Create(preview.Skin, preview.Cape, preview.Profile.Slim)
            : CharacterPreviewModel.Create(_localSkinTexture, _localCapeTexture, UseSlimSkin);

    private void ShowCharacterFront_Click(object sender, RoutedEventArgs e) => SetCharacterView(18);
    private void ShowCharacterBack_Click(object sender, RoutedEventArgs e) => SetCharacterView(180);

    private void SetCharacterView(double yaw)
    {
        CharacterYaw.Angle = yaw;
        CharacterPitch.Angle = 8;
    }

    private void CharacterPreview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement stage || !stage.CaptureMouse()) return;
        _appearanceDragPoint = e.GetPosition(stage);
        e.Handled = true;
    }

    private void CharacterPreview_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { IsMouseCaptured: true } stage) return;
        var point = e.GetPosition(stage);
        var delta = point - _appearanceDragPoint;
        CharacterYaw.Angle = (CharacterYaw.Angle + delta.X * 1.1) % 360;
        CharacterPitch.Angle = Math.Clamp(CharacterPitch.Angle - delta.Y * 0.55, -42, 42);
        _appearanceDragPoint = point;
    }

    private void CharacterPreview_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } stage) stage.ReleaseMouseCapture();
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
            _localCapeTexture = texture.Preview;
            _localCape = texture.Hash;
        }
        else
        {
            SetSkinTexture(texture.Preview);
            _localSkin = texture.Hash;
        }
        if (cape) SetCharacterView(180);
        else SetCharacterView(18);
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
        _localCapeTexture = null;
        NotifyLocalTextures();
    }

    private void NotifyLocalTextures()
    {
        SetPublicPreview(false);
        UpdateCharacterPreview();
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
