using System.Net;
using System.Net.Http;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

internal sealed class PublicMinecraftProfileService(HttpClient? client = null)
{
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 64 * 1024
    };

    internal sealed record Profile(string Name, string Uuid, string? SkinUrl, string? CapeUrl, bool Slim);

    internal async Task<Profile> LookupAsync(string name, CancellationToken ct)
    {
        if (!MinecraftMetadata.IsValidLocalPlayerName(name))
            throw new InvalidDataException("请输入 1–16 位英文字母、数字或下划线组成的 Java 玩家名。");

        var account = await ReadAsync($"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}", ct)
            .ConfigureAwait(false);
        var uuid = String(account, "id");
        if (!Guid.TryParseExact(uuid, "N", out var id) || id == Guid.Empty) throw InvalidProfile();

        var profile = await ReadAsync($"https://sessionserver.mojang.com/session/minecraft/profile/{id:N}", ct)
            .ConfigureAwait(false);
        var canonicalName = String(profile, "name");
        if (canonicalName is null || !MinecraftMetadata.IsValidLocalPlayerName(canonicalName)
            || !string.Equals(canonicalName, name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(String(profile, "id"), uuid, StringComparison.OrdinalIgnoreCase))
            throw InvalidProfile();

        var properties = Property(profile, "properties");
        if (properties.ValueKind == JsonValueKind.Array)
        {
            foreach (var property in properties.EnumerateArray())
            {
                if (String(property, "name") != "textures") continue;
                var payload = String(property, "value");
                if (string.IsNullOrEmpty(payload)) throw InvalidProfile();
                try
                {
                    using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
                    var textures = Property(document.RootElement, "textures");
                    var skin = Property(textures, "SKIN");
                    var cape = Property(textures, "CAPE");
                    return new(canonicalName, id.ToString("D"), TextureUrl(skin), TextureUrl(cape),
                        String(Property(skin, "metadata"), "model") == "slim");
                }
                catch (Exception ex) when (ex is JsonException or FormatException) { throw InvalidProfile(); }
            }
        }
        return new(canonicalName, id.ToString("D"), null, null, false);
    }

    private async Task<JsonElement> ReadAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            using var response = await (client ?? DefaultClient).GetAsync(endpoint, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
                throw new InvalidDataException("找不到这个 Java 玩家，请检查玩家名。");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InvalidDataException("查询太频繁，请稍后再试。");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidProfile();
            return document.RootElement.Clone();
        }
        catch (HttpRequestException)
        { throw new InvalidDataException("无法连接 Minecraft 公开资料服务，请检查网络后重试。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidDataException("查询公开资料超时，请稍后重试。"); }
        catch (JsonException) { throw InvalidProfile(); }
    }

    private static string? TextureUrl(JsonElement texture)
    {
        if (texture.ValueKind == JsonValueKind.Undefined) return null;
        var url = String(texture, "url");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsDefaultPort)
            url = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
        return MicrosoftAccountSession.IsSafeTextureUrl(url) ? url : throw InvalidProfile();
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) ? property : default;

    private static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;

    private static InvalidDataException InvalidProfile() => new("Minecraft 返回的公开资料格式无法识别，请稍后重试。");
}
