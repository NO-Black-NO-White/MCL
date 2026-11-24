using System.Windows;
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
        set { if (Set(ref _useSlimSkin, value)) MarkDirty(); }
    }

    public BitmapSource? LocalAvatar { get => _localAvatar; private set => Set(ref _localAvatar, value); }
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
        try
        {
            if (_localAppearance.PreviewTexture(_localSkin, cape: false) is { } skin)
                LocalAvatar = MinecraftAvatarService.CreateAvatar(skin);
            SetCapePreview(_localAppearance.PreviewTexture(_localCape, cape: true));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or
            UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            LocalAppearanceStatus = "已保存的图片无法预览，请重新选择或清空外观。";
        }
    }

    private void SetCapePreview(BitmapSource? cape)
    {
        if (cape is null) { LocalCapePreview = null; return; }
        var front = new CroppedBitmap(cape, new Int32Rect(1, 1, 10, 16));
        front.Freeze();
        LocalCapePreview = front;
    }

    private void PickLocalSkin_Click(object sender, RoutedEventArgs e) => PickLocalTexture(cape: false);
    private void PickLocalCape_Click(object sender, RoutedEventArgs e) => PickLocalTexture(cape: true);

    private void PickLocalTexture(bool cape)
    {
        if (!CanChooseAccountMode) return;
        var dialog = new OpenFileDialog
        {
            Title = cape ? "选择本地披风" : "选择本地皮肤",
            Filter = "PNG 图片 (*.png)|*.png",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var texture = _localAppearance.ImportTexture(dialog.FileName, cape);
            if (cape)
            {
                _localCape = texture.Hash;
                SetCapePreview(texture.Preview);
            }
            else
            {
                _localSkin = texture.Hash;
                LocalAvatar = MinecraftAvatarService.CreateAvatar(texture.Preview);
            }
            ApplyLocalAppearance = true;
            NotifyLocalTextures();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { LocalAppearanceStatus = ex.Message; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        { LocalAppearanceStatus = "图片无法读取，请重新选择有效的 PNG。"; }
    }

    private void ClearLocalSkin_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChooseAccountMode) return;
        _localSkin = null;
        LocalAvatar = null;
        NotifyLocalTextures();
    }

    private void ClearLocalCape_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChooseAccountMode) return;
        _localCape = null;
        LocalCapePreview = null;
        NotifyLocalTextures();
    }

    private void NotifyLocalTextures()
    {
        Notify(nameof(HasLocalSkin), nameof(HasLocalCape), nameof(LocalSkinLabel), nameof(LocalCapeLabel));
        MarkDirty();
        LocalAppearanceStatus = "已修改外观。点击「保存并使用本地档案」应用。";
    }

    private async void SaveLocalAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChooseAccountMode) return;
        UseMicrosoftAccount = false;
        await RunBusyAsync(() =>
        {
            SaveCurrentSettings();
            LocalAppearanceStatus = "本地档案已保存，下次启动游戏时使用。";
            return Task.CompletedTask;
        });
    }
}
