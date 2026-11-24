using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private readonly IMicrosoftAccountService _accountService;
    private readonly IAccountStore _accountStore;
    private readonly MinecraftAvatarService _avatars = new();
    private MicrosoftAccountSession? _account;
    private CancellationTokenSource? _accountCancellation;
    private CancellationTokenSource? _avatarCancellation;
    private string _microsoftClientId = "";
    private bool _useMicrosoftAccount;
    private bool _isAuthenticating;
    private bool _accountRequiresLogin;
    private string _accountMessage = "登录后可使用自己的 Java 版角色和正版服务器身份。";
    private ImageSource? _accountAvatar;

    public string MicrosoftClientId
    {
        get => _microsoftClientId;
        set { if (Set(ref _microsoftClientId, value)) { MarkDirty(); NotifyAccountState(); } }
    }
    public bool UseMicrosoftAccount
    {
        get => _useMicrosoftAccount;
        set
        {
            if (_loading || (!IsBusy && !IsGameRunning))
                if (Set(ref _useMicrosoftAccount, value)) { MarkDirty(); NotifyAccountState(); }
        }
    }
    public bool UseLocalAccount { get => !UseMicrosoftAccount; set { if (value) UseMicrosoftAccount = false; } }
    public bool HasMicrosoftAccount => _account is not null;
    public bool HasNoMicrosoftAccount => !HasMicrosoftAccount;
    public bool CanSignIn => !IsBusy && !IsGameRunning && IsConfiguredClientId;
    public bool CanSignOut => HasMicrosoftAccount && !IsBusy && !IsGameRunning;
    public bool CanChooseAccountMode => !IsBusy && !IsGameRunning;
    public bool CanEditClientId => !HasMicrosoftAccount && !IsBusy && !IsGameRunning;
    public bool CanCancelAccount => _isAuthenticating && _accountCancellation is { IsCancellationRequested: false };
    public bool IsConfiguredClientId => MicrosoftAccountSession.IsValidClientId(MicrosoftClientId);
    public string SignInButtonText => HasMicrosoftAccount ? "重新登录微软账号" : "使用微软账号登录";
    public string AccountPlayerName => _account?.PlayerName ?? "尚未登录";
    public string AccountProfileId => _account is null ? "登录后会显示你的 Java 版角色信息。" : $"角色 ID：{_account.ProfileId}";
    public string ActivePlayerName => UseMicrosoftAccount ? _account?.PlayerName ?? "未登录" : PlayerName;
    public string ActiveAccountLabel => UseMicrosoftAccount ? (_accountRequiresLogin ? "需要重新登录" : HasMicrosoftAccount ? "微软账号" : "等待微软登录") : "本地档案";
    public string LaunchAccountHint => UseMicrosoftAccount
        ? HasMicrosoftAccount ? "使用微软账号启动 · 启动前联网验证游戏资格" : "请到账号管理中登录拥有 Java 版的微软账号"
        : "本地档案模式 · 可在账号管理中切换微软账号";
    public string AccountStateText => _isAuthenticating ? "正在验证账号…" : _accountRequiresLogin ? "登录已失效，请重新登录" : HasMicrosoftAccount ? "已保存登录 · 启动前重新验证" : "未登录微软账号";
    public string AccountSetupHint => IsConfiguredClientId
        ? "已填写应用编号。需在微软登记桌面回调地址，并取得 Minecraft 接口访问许可。"
        : "首次接入需填写这个启动器自己的微软应用编号。展开下方接入设置，查看申请步骤。";
    public string AccountMessage { get => _accountMessage; private set => Set(ref _accountMessage, value); }
    public ImageSource? AccountAvatar { get => _accountAvatar; private set { if (Set(ref _accountAvatar, value)) Notify(nameof(HasAccountAvatar)); } }
    public bool HasAccountAvatar => AccountAvatar is not null;

    private void LoadSavedAccount()
    {
        try
        {
            _account = _accountStore.Load();
            if (_account is null) return;
            RegisterAccountSecrets(_account);
            if (string.IsNullOrWhiteSpace(_microsoftClientId)) _microsoftClientId = _account.ClientId;
            AccountMessage = "已读取加密保存的角色信息。使用微软账号启动时会联网验证。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            _account = null;
            AccountMessage = "无法读取保存的登录，请重新登录；如需清理旧凭证，可使用下方“清除保存的登录”。";
            _log.Warn("保存的微软登录未能读取，需要重新登录。");
        }
    }

    private void RegisterAccountSecrets(MicrosoftAccountSession account)
        => _log.RegisterSecrets([account.RefreshToken, account.AccessToken]);

    private void NotifyAccountState()
    {
        Notify(nameof(HasMicrosoftAccount), nameof(HasNoMicrosoftAccount), nameof(CanSignIn), nameof(CanSignOut),
            nameof(CanChooseAccountMode), nameof(CanEditClientId), nameof(CanCancelAccount), nameof(IsConfiguredClientId),
            nameof(SignInButtonText), nameof(AccountPlayerName), nameof(AccountProfileId), nameof(ActivePlayerName),
            nameof(ActiveAccountLabel), nameof(LaunchAccountHint), nameof(AccountStateText), nameof(AccountSetupHint),
            nameof(UseMicrosoftAccount), nameof(UseLocalAccount), nameof(SelectedVersionDescription), nameof(CanLaunch),
            nameof(LaunchButtonText), nameof(ActivityText));
    }

    public void OpenAccounts() { AccountsNav.IsChecked = true; Navigate("Accounts"); }
    private void ShowAccounts_Click(object sender, RoutedEventArgs e) => OpenAccounts();

    private async void SaveMicrosoftConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditClientId) return;
        await RunBusyAsync(() =>
        {
            if (!IsConfiguredClientId) throw new InvalidDataException("请输入有效的应用（客户端）ID，例如申请页面显示的带连字符编号。");
            MicrosoftClientId = Guid.Parse(MicrosoftClientId.Trim()).ToString();
            SaveCurrentSettings();
            AccountMessage = "应用编号已保存。完成微软登记和 Minecraft 访问申请后，即可点击上方微软登录。";
            StatusMessage = "微软登录接入设置已保存。";
            return Task.CompletedTask;
        });
    }

    private async void MicrosoftSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSignIn) return;
        await RunBusyAsync(async () =>
        {
            StartAccountOperation();
            try
            {
                AccountMessage = "正在打开微软官方登录页面，请在浏览器中完成授权。";
                var session = await _accountService.SignInAsync(MicrosoftClientId.Trim(), AccountProgress(), _accountCancellation!.Token);
                _accountCancellation.Token.ThrowIfCancellationRequested();
                RegisterAccountSecrets(session);
                _accountStore.Save(session);
                _account = session;
                _accountRequiresLogin = false;
                // This change follows an explicit successful login, while other account controls are disabled.
                _useMicrosoftAccount = true;
                MarkDirty();
                NotifyAccountState();
                try { SaveCurrentSettings(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    _log.Warn("微软账号已加密保存，但启动设置未能保存。");
                    AccountMessage = $"已登录 {session.PlayerName}，并加密保存登录。启动设置未能保存，请稍后重试保存。";
                    StatusMessage = AccountMessage;
                    _ = LoadAccountAvatarAsync();
                    return;
                }
                AccountMessage = $"已登录 {session.PlayerName}，并验证 Java 版游戏资格。可以返回首页启动。";
                StatusMessage = "微软登录成功。";
                _log.Info("微软登录完成，Java 版资格和角色验证通过。");
                _ = LoadAccountAvatarAsync();
            }
            catch (OperationCanceledException)
            {
                AccountMessage = "登录已取消。可以再次点击微软登录。";
                throw;
            }
            catch (Exception ex)
            {
                AccountMessage = ex.Message;
                throw;
            }
            finally { EndAccountOperation(); }
        });
    }

    private void StartAccountOperation()
    {
        _accountCancellation?.Dispose();
        _accountCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _isAuthenticating = true;
        NotifyAccountState();
    }
    private void EndAccountOperation()
    {
        _isAuthenticating = false;
        _accountCancellation?.Dispose();
        _accountCancellation = null;
        NotifyAccountState();
    }
    private IProgress<string> AccountProgress() => new Progress<string>(message =>
    {
        if (_closing || !_isAuthenticating) return;
        AccountMessage = message;
        StatusMessage = message;
    });
    private void CancelAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCancelAccount) return;
        _accountCancellation!.Cancel();
        AccountMessage = "正在取消登录…";
        NotifyAccountState();
    }

    private async void MicrosoftSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChooseAccountMode) return;
        await RunBusyAsync(() =>
        {
            _accountStore.Clear();
            _avatarCancellation?.Cancel();
            _account = null;
            AccountAvatar = null;
            _accountRequiresLogin = false;
            _useMicrosoftAccount = false;
            MarkDirty();
            NotifyAccountState();
            AccountMessage = "已清除 MCL 保存的登录。浏览器中的微软登录状态由浏览器管理。";
            StatusMessage = "已退出当前启动器中的微软账号。";
            _log.Info("已清除当前启动器保存的微软登录。");
            try { SaveCurrentSettings(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AccountMessage += " 启动设置未能保存，请稍后重试。";
                _log.Warn("账号已退出，但启动设置未能保存。");
            }
            return Task.CompletedTask;
        });
    }

    private async Task<GameLaunchIdentity?> PrepareAccountForLaunchAsync()
    {
        if (!UseMicrosoftAccount) return null;
        if (_account is null) throw new InvalidOperationException("请先到账号管理中登录微软账号。");
        StartAccountOperation();
        try
        {
            var session = await _accountService.RefreshAsync(_account, AccountProgress(), _accountCancellation!.Token);
            _accountCancellation.Token.ThrowIfCancellationRequested();
            RegisterAccountSecrets(session);
            _accountStore.Save(session);
            _account = session;
            _accountRequiresLogin = false;
            NotifyAccountState();
            AccountMessage = "Java 版游戏资格和角色验证通过。";
            _ = LoadAccountAvatarAsync();
            return GameLaunchIdentity.FromSession(session);
        }
        catch (AccountAuthenticationException ex)
        {
            if (ex.RequiresLogin) _accountRequiresLogin = true;
            AccountMessage = ex.Message;
            NotifyAccountState();
            throw;
        }
        catch (OperationCanceledException)
        {
            AccountMessage = "账号验证已取消，游戏尚未启动。";
            throw;
        }
        catch (IOException)
        {
            AccountMessage = "验证后的账号信息未能保存，游戏尚未启动。请检查本机账号文件后重试。";
            throw;
        }
        finally { EndAccountOperation(); }
    }

    private async Task LoadAccountAvatarAsync()
    {
        _avatarCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _avatarCancellation = cancellation;
        var session = _account;
        AccountAvatar = null;
        try
        {
            var avatar = await _avatars.LoadAsync(session?.SkinUrl, cancellation.Token);
            if (!_closing && ReferenceEquals(_account, session) && !cancellation.IsCancellationRequested) AccountAvatar = avatar;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or NotSupportedException or ArgumentException or InvalidOperationException)
        { /* A missing avatar must not prevent account use. */ }
        finally { if (ReferenceEquals(_avatarCancellation, cancellation)) _avatarCancellation = null; cancellation.Dispose(); }
    }

    private void OpenMicrosoftSetup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var guide = Path.Combine(AppContext.BaseDirectory, "微软登录接入说明.md");
            if (File.Exists(guide))
            {
                var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
                start.ArgumentList.Add(guide);
                Process.Start(start);
            }
            else Process.Start(new ProcessStartInfo("https://learn.microsoft.com/zh-cn/entra/identity-platform/quickstart-register-app") { UseShellExecute = true });
        }
        catch (Exception ex) { ReportError("无法打开接入说明", ex); }
    }
    private void OpenMinecraftProfile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://www.minecraft.net/msaprofile") { UseShellExecute = true }); }
        catch (Exception ex) { ReportError("无法打开官网", ex); }
    }
}
