using NekoSharp.Core.Models;

namespace NekoSharp.Core.Interfaces;

public interface ICredentialAuthProvider : IInteractiveAuthProvider
{
    Task<AuthSessionState> LoginWithCredentialsAsync(
        string usernameOrEmail,
        string password,
        bool rememberCredentials = true,
        CancellationToken ct = default);

    Task<bool> HasSavedCredentialsAsync(CancellationToken ct = default);

    Task ClearSavedCredentialsAsync(CancellationToken ct = default);
}
