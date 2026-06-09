using System.Text.Json;

namespace NekoSharp.Core.Models;

public sealed class ProviderCookieSession
{
    public string ProviderKey { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string UserAgent { get; init; } = string.Empty;
    public Dictionary<string, string> Cookies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public string? UserDisplayName { get; init; }

    public string CookiesJson
    {
        get => JsonSerializer.Serialize(Cookies);
        init => Cookies = JsonSerializer.Deserialize<Dictionary<string, string>>(value) ??
                          new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
