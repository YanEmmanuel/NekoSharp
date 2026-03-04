using System.Net;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class ProviderCredentialAuthServiceTests
{
    [Fact]
    public async Task LoginWithCredentialsAsync_SavesCredentialSecret()
    {
        var profile = ProviderAuthProfile.CreateMediocreScan();
        var dbPath = CreateTempDbPath();
        var handler = new LoginFlowHandler(token: BuildToken(DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()));

        try
        {
            using var store = new ProviderAuthStore(dbPath);
            using var http = new HttpClient(handler) { BaseAddress = new Uri(profile.ApiBaseUrl) };
            var service = new ProviderAuthService(profile, store: store, httpClient: http);

            var state = await service.LoginWithCredentialsAsync("user@example.com", "secret-123", rememberCredentials: true);

            Assert.True(state.IsAuthenticated);
            Assert.True(await service.HasSavedCredentialsAsync());
            Assert.Equal(1, handler.LoginCalls);
            Assert.Equal(1, handler.MeCalls);
        }
        finally
        {
            CleanupTempDbPath(dbPath);
        }
    }

    [Fact]
    public async Task ApplyAuthHeadersAsync_UsesSavedCredentialWhenTokenMissing()
    {
        var profile = ProviderAuthProfile.CreateMediocreScan();
        var dbPath = CreateTempDbPath();
        var handler = new LoginFlowHandler(token: BuildToken(DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()));

        try
        {
            using var store = new ProviderAuthStore(dbPath);
            await store.SaveLoginSecretAsync(profile.ProviderKey, "saved@example.com", "saved-pass");

            using var http = new HttpClient(handler) { BaseAddress = new Uri(profile.ApiBaseUrl) };
            var service = new ProviderAuthService(profile, store: store, httpClient: http);

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.mediocretoons.site/capitulos/1");
            var token = await service.ApplyAuthHeadersAsync(request);

            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(token, request.Headers.Authorization?.Parameter);
            Assert.Equal(1, handler.LoginCalls);
            Assert.Equal(1, handler.MeCalls);
        }
        finally
        {
            CleanupTempDbPath(dbPath);
        }
    }

    private sealed class LoginFlowHandler : HttpMessageHandler
    {
        private readonly string _token;

        public int LoginCalls { get; private set; }
        public int MeCalls { get; private set; }

        public LoginFlowHandler(string token)
        {
            _token = token;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase))
            {
                LoginCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"token\":\"{_token}\",\"refresh_token\":\"refresh-1\"}}")
                });
            }

            if (path.EndsWith("/usuarios/me", StringComparison.OrdinalIgnoreCase))
            {
                MeCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"nick\":\"tester\",\"email\":\"user@example.com\"}}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not-found")
            });
        }
    }

    private static string BuildToken(long exp)
    {
        var header = Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var payload = Base64Url($"{{\"sub\":1,\"exp\":{exp}}}");
        return $"{header}.{payload}.signature";
    }

    private static string Base64Url(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string CreateTempDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NekoSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "auth.db");
    }

    private static void CleanupTempDbPath(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }
}
