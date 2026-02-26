namespace NekoSharp.Core.Services;

public sealed class ProviderAuthProfile
{
    public required string ProviderKey { get; init; }

    public required string SiteBaseUrl { get; init; }
    public required string SiteLoginUrl { get; init; }

    public required string ApiBaseUrl { get; init; }
    public required string ApiHost { get; init; }

    public required string XAppKeyHeaderValue { get; init; }
    public required string OriginHeaderValue { get; init; }
    public required string RefererHeaderValue { get; init; }

    public string AcceptHeaderValue { get; init; } = "*/*";
    public string ContentTypeHeaderValue { get; init; } = "application/json";

    public string CacheControlHeaderName { get; init; } = "cache-control";
    public string CacheControlHeaderValue { get; init; } = "no-cache";

    public string PragmaHeaderName { get; init; } = "pragma";
    public string PragmaHeaderValue { get; init; } = "no-cache";

    public string XAppKeyHeaderName { get; init; } = "x-app-key";

    public string MeEndpoint { get; init; } = "usuarios/me";
    public string RefreshEndpoint { get; init; } = "auth/refresh";

    public string[] AccessTokenCookieNames { get; init; } = ["token"];
    public string[] RefreshTokenCookieNames { get; init; } = ["refresh_token"];

    public string[] AccessTokenStorageKeys { get; init; } = ["token", "access_token", "accessToken"];
    public string[] RefreshTokenStorageKeys { get; init; } = ["refresh_token", "refreshToken"];

    public static ProviderAuthProfile CreateMediocreScan()
    {
        return new ProviderAuthProfile
        {
            ProviderKey = "mediocrescan",
            SiteBaseUrl = "https://mediocrescan.com",
            SiteLoginUrl = "https://mediocrescan.com/entrar",
            ApiBaseUrl = "https://api.mediocretoons.site/",
            ApiHost = "api.mediocretoons.site",
            XAppKeyHeaderValue = "toons-mediocre-app",
            OriginHeaderValue = "https://mediocrescan.com",
            RefererHeaderValue = "https://mediocrescan.com/"
        };
    }
}
