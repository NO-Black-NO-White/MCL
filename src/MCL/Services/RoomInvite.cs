using MCL.Multiplayer;

namespace MCL.Services;

internal sealed record RoomInvite(string Address, string Code)
{
    private const string TunnelSuffix = ".trycloudflare.com";

    internal static bool IsTunnelHost(string host)
    {
        if (!host.EndsWith(TunnelSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        var label = host.AsSpan(0, host.Length - TunnelSuffix.Length);
        if (label.Length is < 1 or > 63 || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1])) return false;
        foreach (var c in label)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        return true;
    }

    internal static string Create(Uri address, string code) => $"MCL1/{address.Host}/{code}";

    internal static RoomInvite Read(string input, string configuredRelay)
    {
        var value = input.Trim().ToLowerInvariant();
        if (value.Split('/') is ["mcl1", var host, var code] && IsTunnelHost(host) && RoomProtocol.IsToken(code))
            return new("https://" + host, code);
        if (RoomProtocol.IsToken(value))
        {
            if (string.IsNullOrWhiteSpace(configuredRelay))
                throw new InvalidDataException("这个邀请码来自自建中转。请填写联机服务地址，或向房主索取完整邀请码。");
            return new(configuredRelay, value);
        }
        throw new InvalidDataException("邀请码格式有误，请粘贴房主发来的完整邀请码。");
    }
}
