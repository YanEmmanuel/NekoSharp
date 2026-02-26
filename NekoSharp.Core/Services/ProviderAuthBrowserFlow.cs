using System.Net.Http.Headers;
using System.Text.Json;
using NekoSharp.Core.Models;
using PuppeteerSharp;

namespace NekoSharp.Core.Services;

public sealed class ProviderAuthBrowserFlow
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);

    private readonly ProviderAuthProfile _profile;
    private readonly HttpClient _http;
    private readonly LogService? _log;

    public ProviderAuthBrowserFlow(ProviderAuthProfile profile, HttpClient? httpClient = null, LogService? logService = null)
    {
        _profile = profile;
        _log = logService;

        _http = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri(profile.ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<ProviderAuthCredentials> CaptureCredentialsAsync(CancellationToken ct = default)
    {
        return await CaptureCredentialsAsync(DefaultTimeout, ct);
    }

    public async Task<ProviderAuthCredentials> CaptureCredentialsAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] Starting interactive login flow");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);
        var flowToken = linkedCts.Token;

        await using var browser = await LaunchBrowserAsync(flowToken);
        await using var page = await browser.NewPageAsync();

        var userAgent = await page.EvaluateExpressionAsync<string>("navigator.userAgent");

        await page.GoToAsync(_profile.SiteLoginUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle2],
            Timeout = (int)TimeSpan.FromSeconds(60).TotalMilliseconds
        });

        _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] Browser opened. Waiting for token capture...");

        try
        {
            while (!flowToken.IsCancellationRequested)
            {
                var capture = await TryCaptureTokensAsync(page);
                if (!string.IsNullOrWhiteSpace(capture.AccessToken))
                {
                    var credentials = new ProviderAuthCredentials
                    {
                        ProviderKey = _profile.ProviderKey,
                        AccessToken = capture.AccessToken!,
                        RefreshToken = capture.RefreshToken,
                        UserAgent = string.IsNullOrWhiteSpace(userAgent) ? UserAgentProvider.Default : userAgent,
                        Origin = _profile.OriginHeaderValue,
                        Referer = _profile.RefererHeaderValue,
                        XAppKey = _profile.XAppKeyHeaderValue,
                        ObtainedAtUtc = DateTime.UtcNow,
                        ExpiresAtUtc = ProviderAuthService.TryExtractJwtExpirationUtc(capture.AccessToken!),
                        UserJson = null
                    };

                    var userJson = await ValidateTokenAndGetUserJsonAsync(credentials, flowToken);
                    if (!string.IsNullOrWhiteSpace(userJson))
                    {
                        _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] Interactive login succeeded and token is valid");
                        return new ProviderAuthCredentials
                        {
                            ProviderKey = credentials.ProviderKey,
                            AccessToken = credentials.AccessToken,
                            RefreshToken = credentials.RefreshToken,
                            UserAgent = credentials.UserAgent,
                            Origin = credentials.Origin,
                            Referer = credentials.Referer,
                            XAppKey = credentials.XAppKey,
                            ObtainedAtUtc = credentials.ObtainedAtUtc,
                            ExpiresAtUtc = credentials.ExpiresAtUtc,
                            UserJson = userJson
                        };
                    }

                    _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] Captured token is invalid. Waiting for updated session...");
                }

                await Task.Delay(PollInterval, flowToken);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Tempo esgotado aguardando login de {_profile.ProviderKey} no navegador.");
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException($"Login de {_profile.ProviderKey} cancelado.");
        }

        throw new TimeoutException($"Tempo esgotado aguardando login de {_profile.ProviderKey} no navegador.");
    }

    private async Task<IBrowser> LaunchBrowserAsync(CancellationToken ct)
    {
        var options = new LaunchOptions
        {
            Headless = false,
            DefaultViewport = null,
            Args = ["--no-sandbox", "--disable-setuid-sandbox"]
        };

        try
        {
            ct.ThrowIfCancellationRequested();
            return await Puppeteer.LaunchAsync(options);
        }
        catch (Exception ex)
        {
            _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] Failed to launch browser directly ({ex.Message}). Downloading Chromium...");

            var fetcher = new BrowserFetcher();
            var installed = await fetcher.DownloadAsync();
            options.ExecutablePath = fetcher.GetExecutablePath(installed.BuildId);

            ct.ThrowIfCancellationRequested();
            return await Puppeteer.LaunchAsync(options);
        }
    }

    private async Task<(string? AccessToken, string? RefreshToken)> TryCaptureTokensAsync(IPage page)
    {
        try
        {
            var cookies = await page.GetCookiesAsync();
            var accessFromCookie = cookies.FirstOrDefault(c => _profile.AccessTokenCookieNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase))?.Value;
            var refreshFromCookie = cookies.FirstOrDefault(c => _profile.RefreshTokenCookieNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase))?.Value;

            var access = accessFromCookie;
            var refresh = refreshFromCookie;

            if (string.IsNullOrWhiteSpace(access))
                access = await ReadStorageTokenAsync(page, _profile.AccessTokenStorageKeys);

            if (string.IsNullOrWhiteSpace(refresh))
                refresh = await ReadStorageTokenAsync(page, _profile.RefreshTokenStorageKeys);

            return (access, refresh);
        }
        catch (Exception ex)
        {
            _log?.Debug($"[ProviderAuth] [{_profile.ProviderKey}] Token capture polling error: {ex.Message}");
            return (null, null);
        }
    }

    private static async Task<string?> ReadStorageTokenAsync(IPage page, IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys)
        {
            var escaped = key.Replace("'", "\\'");
            var value = await page.EvaluateExpressionAsync<string?>($"window.localStorage.getItem('{escaped}') || window.sessionStorage.getItem('{escaped}')");
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private async Task<string?> ValidateTokenAndGetUserJsonAsync(ProviderAuthCredentials credentials, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _profile.MeEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.TryAddWithoutValidation("Accept", _profile.AcceptHeaderValue);
            request.Headers.TryAddWithoutValidation(_profile.XAppKeyHeaderName, credentials.XAppKey);
            request.Headers.TryAddWithoutValidation("Origin", credentials.Origin);
            request.Headers.Referrer = new Uri(credentials.Referer);
            request.Headers.TryAddWithoutValidation(_profile.CacheControlHeaderName, _profile.CacheControlHeaderValue);
            request.Headers.TryAddWithoutValidation(_profile.PragmaHeaderName, _profile.PragmaHeaderValue);
            request.Headers.TryAddWithoutValidation("User-Agent", credentials.UserAgent);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            _ = JsonDocument.Parse(json);
            return json;
        }
        catch (Exception ex)
        {
            _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] Token validation failed: {ex.Message}");
            return null;
        }
    }
}
