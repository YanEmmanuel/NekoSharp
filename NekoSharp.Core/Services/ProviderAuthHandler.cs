using System.Net;

namespace NekoSharp.Core.Services;

public sealed class ProviderAuthHandler : DelegatingHandler
{
    private readonly IProviderAuthService _authService;
    private readonly ProviderAuthProfile _profile;
    private readonly LogService? _log;

    public ProviderAuthHandler(IProviderAuthService authService, ProviderAuthProfile profile, LogService? logService = null, HttpMessageHandler? inner = null)
        : base(inner ?? new HttpClientHandler())
    {
        _authService = authService;
        _profile = profile;
        _log = logService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!ShouldHandle(request.RequestUri))
            return await base.SendAsync(request, ct);

        var retryRequest = await CloneRequestAsync(request, ct);

        var accessToken = await _authService.ApplyAuthHeadersAsync(request, ct);
        var response = await base.SendAsync(request, ct);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();

        _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] 401 from API. Attempting single retry via refresh/login.");

        var recovered = await _authService.RecoverFromUnauthorizedAsync(accessToken, ct);
        if (!recovered)
            throw new HttpRequestException($"Não foi possível reautenticar no provider {_profile.ProviderKey}.");

        await _authService.ApplyAuthHeadersAsync(retryRequest, ct);
        return await base.SendAsync(retryRequest, ct);
    }

    private bool ShouldHandle(Uri? uri)
    {
        if (uri is null)
            return false;

        return string.Equals(uri.Host, _profile.ApiHost, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var ms = new MemoryStream();
            await request.Content.CopyToAsync(ms, ct);
            ms.Position = 0;

            var streamContent = new StreamContent(ms);
            foreach (var header in request.Content.Headers)
            {
                streamContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = streamContent;
        }

        return clone;
    }
}
