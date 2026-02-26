using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

public sealed class ProviderAuthService : IProviderAuthService
{
    private readonly ProviderAuthProfile _profile;
    private readonly ProviderAuthStore _store;
    private readonly ProviderAuthBrowserFlow _browserFlow;
    private readonly HttpClient _http;
    private readonly LogService? _log;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public ProviderAuthService(
        ProviderAuthProfile profile,
        ProviderAuthStore? store = null,
        ProviderAuthBrowserFlow? browserFlow = null,
        HttpClient? httpClient = null,
        LogService? logService = null)
    {
        _profile = profile;
        _store = store ?? new ProviderAuthStore(logService: logService);
        _browserFlow = browserFlow ?? new ProviderAuthBrowserFlow(profile, logService: logService);
        _log = logService;

        _http = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri(profile.ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<string?> ApplyAuthHeadersAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credentials = await EnsureCredentialsAsync(ct);
        ApplyRequiredHeaders(request, credentials, _profile);
        return credentials.AccessToken;
    }

    public async Task<bool> RecoverFromUnauthorizedAsync(string? failedAccessToken, CancellationToken ct = default)
    {
        _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] Received 401. Trying refresh/login recovery");

        await _authLock.WaitAsync(ct);
        try
        {
            var stored = await _store.TryGetAsync(_profile.ProviderKey, ct);
            if (stored is null)
            {
                _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] No stored credentials after 401. Starting interactive login.");
                await LoginInteractivelyInternalAsync(ct);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(failedAccessToken) &&
                !string.Equals(stored.AccessToken, failedAccessToken, StringComparison.Ordinal))
            {
                _log?.Debug($"[ProviderAuth] [{_profile.ProviderKey}] Token already changed by another request. Retrying with latest token.");
                return true;
            }

            if (await TryRefreshInternalAsync(stored, ct) is { } refreshed)
            {
                _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] Refresh after 401 succeeded");
                return !string.IsNullOrWhiteSpace(refreshed.AccessToken);
            }

            _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] Refresh after 401 failed. Falling back to interactive login.");
            await LoginInteractivelyInternalAsync(ct);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            throw new AuthenticationException($"Login de {_profile.ProviderKey} cancelado.", ex);
        }
        catch (TimeoutException ex)
        {
            throw new AuthenticationException($"Tempo esgotado no login interativo de {_profile.ProviderKey}.", ex);
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<AuthSessionState> GetAuthStateAsync(CancellationToken ct = default)
    {
        var stored = await _store.TryGetAsync(_profile.ProviderKey, ct);
        if (stored is null)
        {
            return new AuthSessionState
            {
                IsAuthenticated = false,
                IsExpired = false,
                Message = "Desconectado"
            };
        }

        var expiresAt = stored.ExpiresAtUtc ?? TryExtractJwtExpirationUtc(stored.AccessToken);
        var now = DateTime.UtcNow;
        var expired = expiresAt.HasValue && expiresAt.Value <= now;

        var (name, email) = TryReadUser(stored.UserJson);

        return new AuthSessionState
        {
            IsAuthenticated = !expired,
            IsExpired = expired,
            ObtainedAtUtc = stored.ObtainedAtUtc,
            ExpiresAtUtc = expiresAt,
            Message = expired ? "Expirado" : "Conectado",
            UserDisplayName = name,
            UserEmail = email,
            UserJson = stored.UserJson
        };
    }

    public async Task<AuthSessionState> LoginInteractivelyAsync(CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            var credentials = await LoginInteractivelyInternalAsync(ct);
            return await BuildStateFromCredentialsAsync(credentials, "Conectado", ct);
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task ClearAuthAsync(CancellationToken ct = default)
    {
        await _store.RemoveAsync(_profile.ProviderKey, ct);
        _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] Stored auth cleared");
    }

    private async Task<ProviderAuthCredentials> EnsureCredentialsAsync(CancellationToken ct)
    {
        var stored = await _store.TryGetAsync(_profile.ProviderKey, ct);
        if (stored is not null && !IsExpired(stored))
            return stored;

        await _authLock.WaitAsync(ct);
        try
        {
            stored = await _store.TryGetAsync(_profile.ProviderKey, ct);
            if (stored is not null && !IsExpired(stored))
                return stored;

            if (stored is not null && await TryRefreshInternalAsync(stored, ct) is { } refreshed)
                return refreshed;

            return await LoginInteractivelyInternalAsync(ct);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<ProviderAuthCredentials> LoginInteractivelyInternalAsync(CancellationToken ct)
    {
        _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] Opening browser for interactive login");

        var captured = await _browserFlow.CaptureCredentialsAsync(ct);
        var validated = await ValidateAndEnrichCredentialsAsync(captured, ct);

        await _store.SaveAsync(validated, ct);
        _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] Interactive login finished");

        return validated;
    }

    private async Task<ProviderAuthCredentials?> TryRefreshInternalAsync(ProviderAuthCredentials current, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken))
            return null;

        _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] Trying token refresh");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _profile.RefreshEndpoint);
            request.Content = JsonContent.Create(new { refresh_token = current.RefreshToken });

            ApplyNonAuthorizationHeaders(request, current, _profile);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] Refresh failed with status {(int)response.StatusCode}");
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var accessToken = TryGetString(root, "token") ??
                              TryGetString(root, "access_token") ??
                              TryGetString(root, "accessToken");

            var refreshToken = TryGetString(root, "refresh_token") ??
                               TryGetString(root, "refreshToken") ??
                               current.RefreshToken;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] Refresh response did not contain access token");
                return null;
            }

            var candidate = new ProviderAuthCredentials
            {
                ProviderKey = _profile.ProviderKey,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                UserAgent = current.UserAgent,
                Origin = current.Origin,
                Referer = current.Referer,
                XAppKey = current.XAppKey,
                ObtainedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = TryExtractJwtExpirationUtc(accessToken),
                UserJson = current.UserJson
            };

            var validated = await ValidateAndEnrichCredentialsAsync(candidate, ct);
            await _store.SaveAsync(validated, ct);
            _log?.Info($"[ProviderAuth] [{_profile.ProviderKey}] Refresh completed successfully");

            return validated;
        }
        catch (Exception ex)
        {
            _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] Refresh exception: {ex.Message}");
            return null;
        }
    }

    private async Task<ProviderAuthCredentials> ValidateAndEnrichCredentialsAsync(ProviderAuthCredentials credentials, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _profile.MeEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        ApplyNonAuthorizationHeaders(request, credentials, _profile);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _log?.Warn($"[ProviderAuth] [{_profile.ProviderKey}] Invalid token while validating {_profile.MeEndpoint} status={(int)response.StatusCode}");
            throw new AuthenticationException($"Token inválido para provider {_profile.ProviderKey} (status {(int)response.StatusCode}). {body}");
        }

        var userJson = await response.Content.ReadAsStringAsync(ct);

        return new ProviderAuthCredentials
        {
            ProviderKey = credentials.ProviderKey,
            AccessToken = credentials.AccessToken,
            RefreshToken = credentials.RefreshToken,
            UserAgent = credentials.UserAgent,
            Origin = credentials.Origin,
            Referer = credentials.Referer,
            XAppKey = credentials.XAppKey,
            ObtainedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = TryExtractJwtExpirationUtc(credentials.AccessToken),
            UserJson = string.IsNullOrWhiteSpace(userJson) ? credentials.UserJson : userJson
        };
    }

    private async Task<AuthSessionState> BuildStateFromCredentialsAsync(ProviderAuthCredentials credentials, string message, CancellationToken ct)
    {
        await _store.SaveAsync(credentials, ct);

        var expiresAt = credentials.ExpiresAtUtc ?? TryExtractJwtExpirationUtc(credentials.AccessToken);
        var (name, email) = TryReadUser(credentials.UserJson);

        return new AuthSessionState
        {
            IsAuthenticated = true,
            IsExpired = false,
            ObtainedAtUtc = credentials.ObtainedAtUtc,
            ExpiresAtUtc = expiresAt,
            Message = message,
            UserDisplayName = name,
            UserEmail = email,
            UserJson = credentials.UserJson
        };
    }

    private static bool IsExpired(ProviderAuthCredentials credentials)
    {
        var exp = credentials.ExpiresAtUtc ?? TryExtractJwtExpirationUtc(credentials.AccessToken);
        if (!exp.HasValue)
            return false;

        return exp.Value <= DateTime.UtcNow.AddSeconds(15);
    }

    internal static void ApplyRequiredHeaders(HttpRequestMessage request, ProviderAuthCredentials credentials, ProviderAuthProfile profile)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        ApplyNonAuthorizationHeaders(request, credentials, profile);
    }

    private static void ApplyNonAuthorizationHeaders(HttpRequestMessage request, ProviderAuthCredentials credentials, ProviderAuthProfile profile)
    {
        request.Headers.Remove("Accept");
        request.Headers.TryAddWithoutValidation("Accept", profile.AcceptHeaderValue);

        request.Content ??= new StringContent(string.Empty, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(profile.ContentTypeHeaderValue);

        request.Headers.Remove(profile.XAppKeyHeaderName);
        request.Headers.TryAddWithoutValidation(profile.XAppKeyHeaderName, credentials.XAppKey);

        request.Headers.Remove("Origin");
        request.Headers.TryAddWithoutValidation("Origin", credentials.Origin);

        request.Headers.Referrer = new Uri(credentials.Referer);

        request.Headers.Remove(profile.CacheControlHeaderName);
        request.Headers.TryAddWithoutValidation(profile.CacheControlHeaderName, profile.CacheControlHeaderValue);

        request.Headers.Remove(profile.PragmaHeaderName);
        request.Headers.TryAddWithoutValidation(profile.PragmaHeaderName, profile.PragmaHeaderValue);

        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", credentials.UserAgent);
    }

    internal static DateTime? TryExtractJwtExpirationUtc(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return null;

        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');

            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("exp", out var expElement))
                return null;

            var exp = expElement.ValueKind switch
            {
                JsonValueKind.Number => expElement.GetInt64(),
                JsonValueKind.String when long.TryParse(expElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0
            };

            if (exp <= 0)
                return null;

            return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static (string? Name, string? Email) TryReadUser(string? userJson)
    {
        if (string.IsNullOrWhiteSpace(userJson))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(userJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                root = data;

            var name = TryGetString(root, "nick") ?? TryGetString(root, "nome") ?? TryGetString(root, "name");
            var email = TryGetString(root, "email");

            return (name, email);
        }
        catch
        {
            return (null, null);
        }
    }
}
