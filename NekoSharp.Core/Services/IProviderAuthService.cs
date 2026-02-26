using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

public interface IProviderAuthService
{
    Task<string?> ApplyAuthHeadersAsync(HttpRequestMessage request, CancellationToken ct = default);
    Task<bool> RecoverFromUnauthorizedAsync(string? failedAccessToken, CancellationToken ct = default);

    Task<AuthSessionState> GetAuthStateAsync(CancellationToken ct = default);
    Task<AuthSessionState> LoginInteractivelyAsync(CancellationToken ct = default);
    Task ClearAuthAsync(CancellationToken ct = default);
}
