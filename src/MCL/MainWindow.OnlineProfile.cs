using System.Windows;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private readonly IMicrosoftAccountService _profileService;
    private readonly IAccountStore _profileStore;
    private MicrosoftAccountSession? _onlineProfile;
    private CancellationTokenSource? _profileOperation;
    private bool _unreadableProfile;
    private string _microsoftClientId = "";
    private string _onlineProfileStatus = "";

    public string MicrosoftClientId
    {
        get => _microsoftClientId;
        set
        {
            if (!Set(ref _microsoftClientId, value)) return;
            MarkDirty();
            NotifyOnlineProfile();
        }
    }

    public bool HasOnlineProfile => _onlineProfile is not null;
    public bool CanSignInForProfile => CanEditLocalAppearance && MicrosoftAccountSession.IsValidClientId(MicrosoftClientId);
    public bool CanRefreshOnlineProfile => CanEditLocalAppearance && _onlineProfile is { } session
        && string.Equals(MicrosoftClientId.Trim(), session.ClientId, StringComparison.OrdinalIgnoreCase);
    public bool CanClearOnlineProfile => CanEditLocalAppearance && (HasOnlineProfile || _unreadableProfile);
    public bool CanCancelProfileOperation => _profileOperation is { IsCancellationRequested: false };
    public bool CanUseOnlineName => CanEditLocalAppearance && HasOnlineProfile;
    public bool CanImportOnlineSkin => CanEditLocalAppearance && _onlineProfile?.SkinUrl is not null;
    public bool CanImportOnlineCape => CanEditLocalAppearance && _onlineProfile?.CapeUrl is not null;
    public string OnlineProfileName => _onlineProfile?.PlayerName ?? "";
    public string OnlineProfileId => _onlineProfile?.ProfileId ?? "";
    public string OnlineAppearanceSummary => (_onlineProfile?.SkinUrl is null ? "无可导入的皮肤" : "当前皮肤可导入")
        + " · " + (_onlineProfile?.CapeUrl is null ? "未佩戴披风" : "当前披风可导入");
    public string OnlineProfileStatus { get => _onlineProfileStatus; private set => Set(ref _onlineProfileStatus, value); }

    private void LoadOnlineProfile(string? clientId)
    {
        _microsoftClientId = clientId ?? "";
        try
        {
            _onlineProfile = _profileStore.Load();
            if (_onlineProfile is null) return;
            if (!MicrosoftAccountSession.IsValidClientId(_microsoftClientId))
                _microsoftClientId = _onlineProfile.ClientId;
            OnlineProfileStatus = "已读取本机保存的角色资料。可以刷新以同步最新外观。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _unreadableProfile = true;
            OnlineProfileStatus = "无法读取保存的登录。可以清除登录记录后重新登录。";
        }
    }

    private void NotifyOnlineProfile() => Notify(nameof(HasOnlineProfile), nameof(CanSignInForProfile),
        nameof(CanRefreshOnlineProfile), nameof(CanClearOnlineProfile), nameof(CanUseOnlineName), nameof(CanImportOnlineSkin),
        nameof(CanImportOnlineCape), nameof(OnlineProfileName), nameof(OnlineProfileId), nameof(OnlineAppearanceSummary));

    private async void SignInForProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CanSignInForProfile) await ReadOnlineProfileAsync(refresh: false);
    }

    private async void RefreshOnlineProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CanRefreshOnlineProfile) await ReadOnlineProfileAsync(refresh: true);
    }

    private Task ReadOnlineProfileAsync(bool refresh) => RunProfileOperationAsync(async ct =>
    {
        var progress = new Progress<string>(message =>
        {
            if (!_closing && _profileOperation?.Token == ct && !ct.IsCancellationRequested)
                OnlineProfileStatus = message;
        });
        OnlineProfileStatus = refresh ? "正在读取最新角色资料…" : "请在微软网页完成授权。";
        var profile = refresh
            ? await _profileService.RefreshAsync(_onlineProfile!, progress, ct)
            : await _profileService.SignInAsync(MicrosoftClientId.Trim(), progress, ct);
        ct.ThrowIfCancellationRequested();
        _profileStore.Save(profile);
        _onlineProfile = profile;
        _unreadableProfile = false;
        NotifyOnlineProfile();
        OnlineProfileStatus = "已获取 Java 版角色资料。可以选择要导入的内容。";
    });

    private void CancelProfileOperation_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCancelProfileOperation) return;
        _profileOperation!.Cancel();
        Notify(nameof(CanCancelProfileOperation));
    }

    private async void ClearOnlineProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanClearOnlineProfile) return;
        await RunBusyAsync(() =>
        {
            _profileStore.Clear();
            _onlineProfile = null;
            _unreadableProfile = false;
            NotifyOnlineProfile();
            OnlineProfileStatus = "已清除 MCL 保存的登录和角色资料。";
            return Task.CompletedTask;
        });
    }

    private void UseOnlineName_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseOnlineName || _onlineProfile is not { } profile) return;
        PlayerName = profile.PlayerName;
        LocalAppearanceStatus = "已填入角色昵称。点击「保存本地角色」应用。";
    }

    private async void ImportOnlineSkin_Click(object sender, RoutedEventArgs e)
    {
        if (CanImportOnlineSkin) await ImportOnlineTextureAsync(cape: false);
    }

    private async void ImportOnlineCape_Click(object sender, RoutedEventArgs e)
    {
        if (CanImportOnlineCape) await ImportOnlineTextureAsync(cape: true);
    }

    private Task ImportOnlineTextureAsync(bool cape) => RunProfileOperationAsync(async ct =>
    {
        var profile = _onlineProfile!;
        var label = cape ? "披风" : "皮肤";
        OnlineProfileStatus = $"正在下载当前{label}…";
        var bytes = await _profileService.DownloadTextureAsync((cape ? profile.CapeUrl : profile.SkinUrl)!, ct);
        ct.ThrowIfCancellationRequested();
        ApplyLocalTexture(_localAppearance.ImportTexture(bytes, cape), cape);
        if (!cape && profile.SkinVariant is "SLIM" or "CLASSIC")
            UseSlimSkin = profile.SkinVariant == "SLIM";
        OnlineProfileStatus = $"已导入{label}。点击上方「保存本地角色」用于下次启动。";
    });

    private Task RunProfileOperationAsync(Func<CancellationToken, Task> action) => RunBusyAsync(async () =>
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _profileOperation = operation;
        Notify(nameof(CanCancelProfileOperation));
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (!_closing) OnlineProfileStatus = "操作已取消。"; }
        catch (Exception ex) when (ex is AccountAuthenticationException or IOException or InvalidDataException or UnauthorizedAccessException)
        { OnlineProfileStatus = ex.Message; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or System.Runtime.InteropServices.COMException)
        { OnlineProfileStatus = "无法读取官方返回的图片，请刷新角色资料后重试。"; }
        finally
        {
            _profileOperation = null;
            Notify(nameof(CanCancelProfileOperation));
        }
    });
}
