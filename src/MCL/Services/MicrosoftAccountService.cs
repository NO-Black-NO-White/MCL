using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public interface IMicrosoftAccountService
{
    Task<MicrosoftAccountSession> SignInAsync(string clientId, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<MicrosoftAccountSession> RefreshAsync(MicrosoftAccountSession session, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadTextureAsync(string url, CancellationToken cancellationToken = default);
}

public sealed class AccountAuthenticationException : Exception
{
    public bool RequiresLogin { get; }
    public AccountAuthenticationException(string message, bool requiresLogin = false) : base(message) => RequiresLogin = requiresLogin;
}

/// <summary>Microsoft authorization -> Xbox -> Minecraft. This service never writes credentials to disk.</summary>
public sealed class MicrosoftAccountService : IMicrosoftAccountService, IDisposable
{
    public const string Scopes = "XboxLive.signin offline_access";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XboxEndpoint = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsEndpoint = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftEndpoint = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string ProfileEndpoint = "https://api.minecraftservices.com/minecraft/profile";
    private const int MaximumResponseBytes = 1_048_576;
    private readonly HttpClient _client;
    private readonly IMicrosoftBrowserLogin _browserLogin;
    private readonly bool _ownsClient;
    private bool _disposed;

    public MicrosoftAccountService(HttpClient? client = null, IMicrosoftBrowserLogin? browserLogin = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };
        _browserLogin = browserLogin ?? new MicrosoftBrowserLogin();
    }

    public async Task<MicrosoftAccountSession> SignInAsync(string clientId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        clientId = NormalizeClientId(clientId);
        progress?.Report("请在打开的微软官方页面完成登录和授权…");
        var authorization = await _browserLogin.AuthorizeAsync(clientId, cancellationToken).ConfigureAwait(false);
        if (authorization.Code.Length is < 1 or > 16_384 || authorization.Code.Any(char.IsControl)
            || authorization.CodeVerifier.Length is < 43 or > 128
            || !Uri.TryCreate(authorization.RedirectUri, UriKind.Absolute, out var redirect)
            || redirect.Scheme != Uri.UriSchemeHttp || redirect.Host != "localhost" || redirect.Port is < 1 or > 65535
            || redirect.AbsolutePath != "/" || redirect.Query.Length != 0 || redirect.Fragment.Length != 0 || redirect.UserInfo.Length != 0)
            throw new AccountAuthenticationException("微软授权回调无效，请重新登录。", requiresLogin: true);
        using var response = await SendAsync(HttpMethod.Post, TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["grant_type"] = "authorization_code", ["code"] = authorization.Code,
            ["redirect_uri"] = authorization.RedirectUri, ["code_verifier"] = authorization.CodeVerifier, ["scope"] = Scopes
        }), null, "微软授权", cancellationToken).ConfigureAwait(false);
        var token = RequiredToken(response.RootElement, "access_token");
        var refresh = RequiredToken(response.RootElement, "refresh_token");
        return await CompleteMinecraftAsync(clientId, token, refresh, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MicrosoftAccountSession> RefreshAsync(MicrosoftAccountSession session, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { session.Validate(); }
        catch (InvalidDataException) { throw new AccountAuthenticationException("保存的账号信息无效，请重新进行微软登录。", requiresLogin: true); }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("正在刷新微软登录…");
        using var response = await SendAsync(HttpMethod.Post, TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = NormalizeClientId(session.ClientId), ["grant_type"] = "refresh_token",
            ["refresh_token"] = session.RefreshToken, ["scope"] = Scopes
        }), null, "微软登录续期", cancellationToken).ConfigureAwait(false);
        var token = RequiredToken(response.RootElement, "access_token");
        var refresh = OptionalString(response.RootElement, "refresh_token") ?? session.RefreshToken;
        if (!MicrosoftAccountSession.IsToken(refresh)) throw BadResponse();
        var renewed = await CompleteMinecraftAsync(session.ClientId, token, refresh, progress, cancellationToken).ConfigureAwait(false);
        if (!renewed.ProfileId.Equals(session.ProfileId, StringComparison.OrdinalIgnoreCase))
            throw new AccountAuthenticationException("续期后的游戏身份与保存的账号不一致，请重新登录。", requiresLogin: true);
        return renewed;
    }

    private async Task<MicrosoftAccountSession> CompleteMinecraftAsync(string clientId, string microsoftToken, string refreshToken,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("正在验证 Xbox 账号…");
        using var xbox = await SendAsync(HttpMethod.Post, XboxEndpoint, JsonContent(new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftToken },
            RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT"
        }), null, "Xbox 登录", ct).ConfigureAwait(false);
        var userToken = RequiredToken(xbox.RootElement, "Token");
        var userClaims = UserClaims(xbox.RootElement);
        var userHash = RequiredToken(userClaims, "uhs");
        if (userHash.Length > 32 || userHash.Any(c => c is < '0' or > '9')) throw BadResponse();

        using var xsts = await SendAsync(HttpMethod.Post, XstsEndpoint, JsonContent(new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { userToken } },
            RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT"
        }), null, "Xbox 游戏授权", ct).ConfigureAwait(false);
        var xstsToken = RequiredToken(xsts.RootElement, "Token");
        var xstsClaims = UserClaims(xsts.RootElement);
        if (RequiredToken(xstsClaims, "uhs") != userHash)
            throw new AccountAuthenticationException("Xbox 返回的账号身份不一致，请重新登录。", requiresLogin: true);
        var xuid = OptionalString(xstsClaims, "xid") ?? OptionalString(userClaims, "xid") ?? "";
        if (xuid.Length > 20 || xuid.Any(c => c is < '0' or > '9')) throw BadResponse();

        progress?.Report("正在登录 Minecraft 服务…");
        var issuedAt = DateTimeOffset.UtcNow;
        using var minecraft = await SendAsync(HttpMethod.Post, MinecraftEndpoint, JsonContent(new
        {
            identityToken = "XBL3.0 x=" + userHash + ";" + xstsToken,
            ensureLegacyEnabled = true
        }), null, "Minecraft 登录", ct).ConfigureAwait(false);
        var minecraftToken = RequiredToken(minecraft.RootElement, "access_token");
        if (!minecraft.RootElement.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out var seconds) || seconds is < 1 or > 604_800)
            throw BadResponse();

        progress?.Report("正在验证 Minecraft Java 版游戏资格…");
        var licenseEndpoint = "https://api.minecraftservices.com/entitlements/license?requestId=" + Guid.NewGuid().ToString("D");
        using var entitlements = await SendAsync(HttpMethod.Get, licenseEndpoint, null, minecraftToken, "游戏资格验证", ct).ConfigureAwait(false);
        if (!entitlements.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) throw BadResponse();
        if (!items.EnumerateArray().Any(item => OptionalString(item, "name") is "game_minecraft" or "product_minecraft"))
            throw new AccountAuthenticationException("此微软账号没有可用的 Minecraft Java 版游戏资格。请确认登录的是购买游戏或拥有有效相关订阅的账号。", requiresLogin: true);

        progress?.Report("正在读取你的 Minecraft 角色…");
        using var profile = await SendAsync(HttpMethod.Get, ProfileEndpoint, null, minecraftToken, "角色信息读取", ct).ConfigureAwait(false);
        var profileId = OptionalString(profile.RootElement, "id") ?? "";
        var playerName = OptionalString(profile.RootElement, "name") ?? "";
        var (skinUrl, skinVariant) = ActiveTexture(profile.RootElement, "skins");
        var (capeUrl, _) = ActiveTexture(profile.RootElement, "capes");
        var result = new MicrosoftAccountSession
        {
            ClientId = NormalizeClientId(clientId), RefreshToken = refreshToken, AccessToken = minecraftToken,
            ExpiresAt = issuedAt.AddSeconds(seconds), ProfileId = profileId.ToLowerInvariant(), PlayerName = playerName,
            SkinUrl = skinUrl, SkinVariant = skinVariant, CapeUrl = capeUrl, XboxUserId = xuid
        };
        try { result.Validate(); }
        catch (InvalidDataException) { throw BadResponse(); }
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static (string? Url, string? Variant) ActiveTexture(JsonElement profile, string property)
    {
        if (!profile.TryGetProperty(property, out var textures) || textures.ValueKind != JsonValueKind.Array)
            return (null, null);
        foreach (var texture in textures.EnumerateArray())
        {
            if (OptionalString(texture, "state") != "ACTIVE") continue;
            var url = OptionalString(texture, "url");
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
                && uri.Host == "textures.minecraft.net" && uri.IsDefaultPort)
                url = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
            if (MicrosoftAccountSession.IsSafeTextureUrl(url))
                return (url, OptionalString(texture, "variant"));
        }
        return (null, null);
    }

    public async Task<byte[]> DownloadTextureAsync(string url, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!MicrosoftAccountSession.IsSafeTextureUrl(url))
            throw new InvalidDataException("外观地址无效，请刷新角色资料后重试。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var buffer = new MemoryStream();
            await ReadBoundedAsync(response.Content, buffer, timeout.Token).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (HttpRequestException)
        { throw new AccountAuthenticationException("无法从 Minecraft 下载外观，请刷新资料或稍后重试。"); }
        catch (IOException)
        { throw new AccountAuthenticationException("下载 Minecraft 外观时连接中断，请重试。"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AccountAuthenticationException("下载 Minecraft 外观超时，请稍后重试。"); }
    }

    private static async Task ReadBoundedAsync(HttpContent content, MemoryStream buffer, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) throw BadResponse();
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var block = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(block, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumResponseBytes) throw BadResponse();
            buffer.Write(block, 0, count);
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string endpoint, HttpContent? content, string? bearer,
        string stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("MCL/0.4");
        if (new Uri(endpoint).Host.EndsWith(".xboxlive.com", StringComparison.Ordinal))
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await ReadBoundedAsync(response.Content, buffer, timeout.Token).ConfigureAwait(false);
            JsonDocument? document = null;
            try { document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { if (response.IsSuccessStatusCode) throw BadResponse(); }
            if (!response.IsSuccessStatusCode)
            {
                var error = ExplainFailure(response.StatusCode, endpoint, document?.RootElement, stage);
                document?.Dispose();
                throw error;
            }
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            { document?.Dispose(); throw BadResponse(); }
            return document;
        }
        catch (HttpRequestException)
        { throw new AccountAuthenticationException($"{stage}时无法连接官方服务，请检查网络后重试。"); }
        catch (IOException)
        { throw new AccountAuthenticationException($"{stage}时连接中断，请检查网络后重试。"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AccountAuthenticationException($"{stage}超时，请检查网络后重试。"); }
    }

    private static AccountAuthenticationException ExplainFailure(HttpStatusCode status, string endpoint, JsonElement? body, string stage)
    {
        var error = body.HasValue ? OptionalString(body.Value, "error") : null;
        if (endpoint == TokenEndpoint && error is "invalid_grant" or "interaction_required" or "login_required" or "consent_required")
            return new("登录已过期或授权已撤销，请重新进行微软登录。", requiresLogin: true);
        if (endpoint == TokenEndpoint && error is "invalid_client" or "unauthorized_client" or "invalid_scope" or "invalid_request")
            return new("微软应用配置未通过验证，请检查本启动器的应用 ID、个人微软账号支持和 http://localhost 桌面回调配置。");
        if (endpoint == XstsEndpoint && body.HasValue && body.Value.ValueKind == JsonValueKind.Object
            && body.Value.TryGetProperty("XErr", out var xerr) && xerr.ValueKind == JsonValueKind.Number && xerr.TryGetInt64(out var code))
        {
            var message = code switch
            {
                2148916233 => "此微软账号尚未建立 Xbox 档案，请先在 Xbox 官方网站完成账号设置，再重新登录。",
                2148916234 => "请先在 Xbox 官方网站接受服务条款，再重新登录。",
                2148916235 => "Xbox 服务暂不支持此账号所在地区。",
                2148916236 => "此账号需要先在微软官方页面完成年龄验证。",
                2148916237 => "此账号受到 Xbox 游玩时间限制，请检查家庭设置。",
                2148916238 => "此未成年账号需要先完成微软家庭和 Xbox 权限设置。",
                2148916227 => "此账号受到 Xbox 服务限制，请联系官方支持。",
                2148916229 => "此账号的家庭权限尚未允许所需的 Xbox 服务，请由监护人检查设置。",
                _ => "Xbox 未能授予游戏权限，请检查账号设置后重试。"
            };
            return new(message, requiresLogin: true);
        }
        if (endpoint == MinecraftEndpoint && status == HttpStatusCode.Forbidden)
            return new("Minecraft 服务拒绝了登录请求。请确认填写的启动器应用 ID 已通过 Minecraft 官方接入审核。");
        if (endpoint == ProfileEndpoint && status == HttpStatusCode.NotFound)
            return new("账号尚未创建 Minecraft Java 版角色，请先在 Minecraft 官网设置角色名，再重新登录。", requiresLogin: true);
        if ((int)status == 429) return new("官方服务请求过于频繁，请稍候再试。");
        if ((int)status >= 500) return new($"{stage}的官方服务暂时不可用，请稍后重试。");
        if (status == HttpStatusCode.Unauthorized) return new($"{stage}未通过身份验证，请重新进行微软登录。", requiresLogin: true);
        if ((int)status is >= 300 and < 400) return new($"{stage}返回了意外跳转，已停止传送账号凭证，请稍后重试。");
        return new($"{stage}未能完成（HTTP {(int)status}），请检查账号和应用配置后重试。");
    }

    private static JsonElement UserClaims(JsonElement root)
    {
        if (root.TryGetProperty("DisplayClaims", out var claims) && claims.ValueKind == JsonValueKind.Object
            && claims.TryGetProperty("xui", out var users) && users.ValueKind == JsonValueKind.Array && users.GetArrayLength() == 1
            && users[0].ValueKind == JsonValueKind.Object) return users[0];
        throw BadResponse();
    }
    private static string RequiredToken(JsonElement root, string name)
    {
        var value = OptionalString(root, name);
        if (!MicrosoftAccountSession.IsToken(value)) throw BadResponse();
        return value!;
    }
    private static string? OptionalString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static StringContent JsonContent<T>(T data) => new(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
    private static string NormalizeClientId(string clientId)
    {
        if (!MicrosoftAccountSession.IsValidClientId(clientId))
            throw new AccountAuthenticationException("请先配置本启动器自己的微软应用 ID，再进行微软登录。");
        return Guid.Parse(clientId.Trim()).ToString("D");
    }
    private static AccountAuthenticationException BadResponse() => new("官方登录服务返回的信息不完整或格式无效，请稍后重试。");
    public void Dispose() { if (_disposed) return; _disposed = true; if (_ownsClient) _client.Dispose(); }
}
