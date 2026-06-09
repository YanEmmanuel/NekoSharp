namespace NekoSharp.Core.Interfaces;

public interface IAuthenticatedRequestProvider
{
    Task ApplyRequestAuthenticationAsync(
        HttpRequestMessage request,
        CancellationToken ct = default);
}
