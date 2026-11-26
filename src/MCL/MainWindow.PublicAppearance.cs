using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private readonly PublicMinecraftProfileService _publicProfiles = new();
    private sealed record PublicAppearance(PublicMinecraftProfileService.Profile Profile,
        byte[]? SkinBytes, byte[]? CapeBytes, BitmapSource? Skin, BitmapSource? Cape);

    private PublicAppearance? _publicAppearance;
    private bool _previewingPublicAppearance;
    private string _publicPlayerName = "";
    private string _publicAppearanceStatus = "";

    public string PublicPlayerName
    {
        get => _publicPlayerName;
        set
        {
            if (!Set(ref _publicPlayerName, value)) return;
            var wasPreviewing = IsPreviewingPublicAppearance;
            ClearPublicAppearance();
            if (wasPreviewing) UpdateCharacterPreview();
            PublicAppearanceStatus = "";
        }
    }

    public string PublicAppearanceStatus { get => _publicAppearanceStatus; private set => Set(ref _publicAppearanceStatus, value); }
    public bool HasPublicAppearance => _publicAppearance is not null;
    public bool HasPublicTextures => _publicAppearance is { } result && (result.Skin is not null || result.Cape is not null);
    public bool IsPreviewingPublicAppearance => _previewingPublicAppearance;
    public string PublicAppearanceName => _publicAppearance?.Profile.Name ?? "";
    public string PublicAppearanceUuid => _publicAppearance?.Profile.Uuid ?? "";

    private void ClearPublicAppearance()
    {
        _publicAppearance = null;
        SetPublicPreview(false);
        NotifyPublicAppearance();
    }

    private void NotifyPublicAppearance() => Notify(nameof(HasPublicAppearance), nameof(HasPublicTextures),
        nameof(PublicAppearanceName), nameof(PublicAppearanceUuid));

    private void SetPublicPreview(bool value)
    {
        if (Set(ref _previewingPublicAppearance, value, nameof(IsPreviewingPublicAppearance)))
            Notify(nameof(PublicPreviewButtonLabel));
    }

    public string PublicPreviewButtonLabel => IsPreviewingPublicAppearance ? "返回本地预览" : "预览查询结果";

    private async void LookupPublicAppearance_Click(object sender, RoutedEventArgs e) => await LookupPublicAppearanceAsync();

    private async void PublicPlayerNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LookupPublicAppearanceAsync();
    }

    private Task LookupPublicAppearanceAsync()
    {
        if (!CanEditLocalAppearance) return Task.CompletedTask;
        var name = PublicPlayerName.Trim();
        return RunBusyAsync(async () =>
        {
            ClearPublicAppearance();
            UpdateCharacterPreview();
            PublicAppearanceStatus = "正在查询 Minecraft 公开资料…";
            var ct = _lifetime.Token;
            try
            {
                var profile = await _publicProfiles.LookupAsync(name, ct);
                var skinDownload = DownloadPublicTextureAsync(profile.SkinUrl, ct);
                var capeDownload = DownloadPublicTextureAsync(profile.CapeUrl, ct);
                await Task.WhenAll(skinDownload, capeDownload);
                ct.ThrowIfCancellationRequested();
                if (!string.Equals(PublicPlayerName.Trim(), name, StringComparison.Ordinal)) return;

                var skin = await skinDownload;
                var cape = await capeDownload;
                _publicAppearance = new(profile, skin, cape,
                    skin is null ? null : LocalAppearanceService.DecodeTexture(skin, cape: false),
                    cape is null ? null : LocalAppearanceService.DecodeTexture(cape, cape: true));
                SetPublicPreview(HasPublicTextures);
                NotifyPublicAppearance();
                UpdateCharacterPreview();
                SetCharacterView(skin is null && cape is not null ? 180 : 18);

                var skinLabel = skin is null ? "无公开皮肤" : "当前皮肤可导入";
                var capeLabel = cape is null ? "未佩戴披风" : "当前披风可导入";
                PublicAppearanceStatus = $"{skinLabel} · {capeLabel}";
            }
            catch (Exception ex) when (ex is AccountAuthenticationException or IOException or InvalidDataException)
            { PublicAppearanceStatus = ex.Message; }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
            { PublicAppearanceStatus = "无法预览返回的 PNG，请稍后重试。"; }
        });
    }

    private async Task<byte[]?> DownloadPublicTextureAsync(string? url, CancellationToken ct) =>
        url is null ? null : await _profileService.DownloadTextureAsync(url, ct);

    private void TogglePublicAppearancePreview_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditLocalAppearance || !HasPublicTextures) return;
        SetPublicPreview(!IsPreviewingPublicAppearance);
        UpdateCharacterPreview();
        SetCharacterView(IsPreviewingPublicAppearance && _publicAppearance?.Skin is null ? 180 : 18);
    }

    private void UsePublicPlayerName_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditLocalAppearance || _publicAppearance is not { } result) return;
        PlayerName = result.Profile.Name;
        LocalAppearanceStatus = "已填入玩家名。点击「保存本地角色」应用。";
    }

    private void ImportPublicAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditLocalAppearance || !HasPublicTextures || _publicAppearance is not { } result) return;
        try
        {
            var skin = result.SkinBytes is { } skinBytes ? _localAppearance.ImportTexture(skinBytes, cape: false) : ((string Hash, BitmapSource Preview)?)null;
            var cape = result.CapeBytes is { } capeBytes ? _localAppearance.ImportTexture(capeBytes, cape: true) : ((string Hash, BitmapSource Preview)?)null;

            if (skin is { } newSkin)
            {
                _localSkin = newSkin.Hash;
                SetSkinTexture(newSkin.Preview);
                Set(ref _useSlimSkin, result.Profile.Slim, nameof(UseSlimSkin));
            }
            if (cape is { } newCape)
            {
                _localCape = newCape.Hash;
                _localCapeTexture = newCape.Preview;
            }
            ApplyLocalAppearance = true;
            NotifyLocalTextures();
            PublicAppearanceStatus = "已导入外观，点击「保存本地角色」应用。";
            if (skin is null) SetCharacterView(180);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { PublicAppearanceStatus = ex.Message; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        { PublicAppearanceStatus = "无法导入这组 PNG，请重新查询后再试。"; }
    }
}
