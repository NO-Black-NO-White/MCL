using System.Text.RegularExpressions;

namespace MCL.Models;

/// <summary>Credentials for one online launch. Never format credentials into logs.</summary>
public sealed class GameLaunchIdentity
{
    public string PlayerName { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string XboxUserId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }

    public static GameLaunchIdentity FromSession(MicrosoftAccountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var identity = new GameLaunchIdentity
        {
            PlayerName = session.PlayerName,
            ProfileId = session.ProfileId,
            AccessToken = session.AccessToken,
            XboxUserId = session.XboxUserId,
            ClientId = session.ClientId,
            ExpiresAt = session.ExpiresAt
        };
        identity.Validate();
        return identity;
    }

    public void Validate()
    {
        if (!Regex.IsMatch(PlayerName ?? "", "\\A[A-Za-z0-9_]{1,16}\\z", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(ProfileId ?? "", "\\A[0-9a-fA-F]{32}\\z", RegexOptions.CultureInvariant)
            || !MicrosoftAccountSession.IsValidClientId(ClientId)
            || !MicrosoftAccountSession.IsToken(AccessToken)
            || !Regex.IsMatch(XboxUserId ?? "", "\\A[0-9]{0,20}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("正版账号信息不完整，请退出账号后重新登录。");
        if (ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
            throw new InvalidOperationException("正版登录已过期，请联网刷新登录后再启动游戏。");
    }

    public override string ToString() => "Minecraft 正版启动身份（凭证已隐藏）";
}
