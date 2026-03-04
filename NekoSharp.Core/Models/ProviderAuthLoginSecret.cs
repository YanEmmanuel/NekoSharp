namespace NekoSharp.Core.Models;

public sealed class ProviderAuthLoginSecret
{
    public string ProviderKey { get; init; } = string.Empty;
    public string UsernameOrEmail { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}
