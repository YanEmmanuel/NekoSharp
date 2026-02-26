namespace NekoSharp.Core.Models;

public sealed class ProviderAuthCredentials
{
    public string ProviderKey { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public string? RefreshToken { get; init; }
    public string UserAgent { get; init; } = string.Empty;
    public string Origin { get; init; } = string.Empty;
    public string Referer { get; init; } = string.Empty;
    public string XAppKey { get; init; } = string.Empty;
    public DateTime ObtainedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; init; }
    public string? UserJson { get; init; }

    public bool IsExpired(DateTime utcNow)
    {
        if (!ExpiresAtUtc.HasValue)
            return false;

        return ExpiresAtUtc.Value <= utcNow;
    }
}
