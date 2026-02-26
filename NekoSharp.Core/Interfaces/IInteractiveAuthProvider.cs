using NekoSharp.Core.Models;

namespace NekoSharp.Core.Interfaces;

public interface IInteractiveAuthProvider
{
    Task<AuthSessionState> GetAuthStateAsync(CancellationToken ct = default);
    Task<AuthSessionState> LoginInteractivelyAsync(CancellationToken ct = default);
    Task ClearAuthAsync(CancellationToken ct = default);
}
