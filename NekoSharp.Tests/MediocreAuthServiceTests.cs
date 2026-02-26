using System.Net.Http.Headers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class ProviderAuthServiceTests
{
    [Fact]
    public void ApplyRequiredHeaders_AddsMandatoryHeadersAndBearer()
    {
        var profile = ProviderAuthProfile.CreateMediocreScan();
        var credentials = new ProviderAuthCredentials
        {
            ProviderKey = profile.ProviderKey,
            AccessToken = "token-123",
            RefreshToken = "refresh-123",
            UserAgent = "Mozilla/5.0 Test",
            Origin = profile.OriginHeaderValue,
            Referer = profile.RefererHeaderValue,
            XAppKey = profile.XAppKeyHeaderValue,
            ObtainedAtUtc = DateTime.UtcNow
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.mediocretoons.site/capitulos/1");

        ProviderAuthService.ApplyRequiredHeaders(request, credentials, profile);

        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(credentials.AccessToken, request.Headers.Authorization.Parameter);

        Assert.True(request.Headers.TryGetValues("x-app-key", out var xAppValues));
        Assert.Contains(credentials.XAppKey, xAppValues);

        Assert.True(request.Headers.TryGetValues("Origin", out var originValues));
        Assert.Contains(credentials.Origin, originValues);

        Assert.True(request.Headers.TryGetValues("cache-control", out var ccValues));
        Assert.Contains("no-cache", ccValues);

        Assert.True(request.Headers.TryGetValues("pragma", out var pragmaValues));
        Assert.Contains("no-cache", pragmaValues);

        Assert.Equal(new Uri(credentials.Referer), request.Headers.Referrer);

        var hasContentTypeHeader = request.Headers.TryGetValues("content-type", out var ctValues)
                                   && ctValues.Contains("application/json");
        var hasContentTypeContent = string.Equals(
            request.Content?.Headers.ContentType?.MediaType,
            "application/json",
            StringComparison.OrdinalIgnoreCase);
        Assert.True(hasContentTypeHeader || hasContentTypeContent);
    }

    [Fact]
    public void TryExtractJwtExpirationUtc_ParsesExpClaim()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds();
        var token = BuildToken(exp);

        var parsed = ProviderAuthService.TryExtractJwtExpirationUtc(token);

        Assert.True(parsed.HasValue);
        Assert.InRange(parsed!.Value, DateTime.UtcNow.AddMinutes(44), DateTime.UtcNow.AddMinutes(46));
    }

    [Fact]
    public void TryExtractJwtExpirationUtc_InvalidToken_ReturnsNull()
    {
        var parsed = ProviderAuthService.TryExtractJwtExpirationUtc("invalid.token");
        Assert.Null(parsed);
    }

    private static string BuildToken(long exp)
    {
        var header = Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var payload = Base64Url($"{{\"sub\":1,\"exp\":{exp}}}");
        var signature = "signature";
        return $"{header}.{payload}.{signature}";
    }

    private static string Base64Url(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
