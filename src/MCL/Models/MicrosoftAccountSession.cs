using System.Text.RegularExpressions;

namespace MCL.Models;

/// <summary>Credentials are serialized only by the Windows-protected account store.</summary>
public sealed class MicrosoftAccountSession
{
    public string ClientId { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string PlayerName { get; init; } = "";
    public string? SkinUrl { get; init; }
    public string XboxUserId { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }

    public static bool IsValidClientId(string? value) => Guid.TryParseExact(value?.Trim(), "D", out var id) && id != Guid.Empty;

    public void Validate()
    {
        if (!IsValidClientId(ClientId)
            || !Regex.IsMatch(ProfileId ?? "", "\\A[0-9a-fA-F]{32}\\z", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(PlayerName ?? "", "\\A[A-Za-z0-9_]{1,16}\\z", RegexOptions.CultureInvariant)
            || !IsToken(AccessToken) || !IsToken(RefreshToken)
            || ExpiresAt == default
            || !Regex.IsMatch(XboxUserId ?? "", "\\A[0-9]{0,20}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("保存的账号信息不完整，请重新进行微软登录。");
        if (SkinUrl is not null && !IsSafeSkinUrl(SkinUrl))
            throw new InvalidDataException("账号皮肤地址无效，请重新进行微软登录。");
    }

    public static bool IsSafeSkinUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment) && Regex.IsMatch(uri.AbsolutePath, "\\A/texture/[0-9a-fA-F]{32,128}\\z", RegexOptions.CultureInvariant);

    internal static bool IsToken(string? value) => value is { Length: > 0 and <= 131_072 } && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);

    // Do not let diagnostic interpolation expose either credential.
    public override string ToString() => "MicrosoftAccountSession（登录凭证已隐藏）";
}
