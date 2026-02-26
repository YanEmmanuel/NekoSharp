namespace NekoSharp.Core.Models;

public sealed class AuthSessionState
{
    public bool IsAuthenticated { get; init; }
    public bool IsExpired { get; init; }
    public DateTime? ObtainedAtUtc { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? UserDisplayName { get; init; }
    public string? UserEmail { get; init; }
    public string? UserJson { get; init; }
}
