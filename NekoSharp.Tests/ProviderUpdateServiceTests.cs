using System.Net;
using System.Security.Cryptography;
using System.Text;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class ProviderUpdateServiceTests
{
    private const string OfficialMainManifestUrl = "https://raw.githubusercontent.com/YanEmmanuel/NekoSharp/main/providers/manifest.json";

    [Fact]
    public async Task UpdateProvidersAsync_LegacyManifestUrl_MigratesAndUsesOfficialFallback()
    {
        var settings = new InMemorySettingsStore();
        await settings.SetStringAsync(ProviderUpdateService.SettingsManifestUrlKey, OfficialMainManifestUrl);

        var handler = new StubHttpMessageHandler();
        handler.AddText(ProviderUpdateService.DefaultManifestUrl, "404: Not Found", HttpStatusCode.NotFound);
        handler.AddJson(OfficialMainManifestUrl, "{ \"providers\": [] }");

        using var http = new HttpClient(handler);
        var providersDirectory = CreateTempDirectory();

        try
        {
            var service = new ProviderUpdateService(
                settings,
                httpClient: http,
                providersDirectory: providersDirectory);

            var result = await service.UpdateProvidersAsync();

            Assert.Equal(0, result.DownloadedCount);
            Assert.Equal(0, result.SkippedCount);
            Assert.Contains("Manifesto carregado, mas sem providers para atualizar.", result.Message);

            var persistedManifest = await settings.GetStringAsync(ProviderUpdateService.SettingsManifestUrlKey);
            Assert.Equal(ProviderUpdateService.DefaultManifestUrl, persistedManifest);

            Assert.Contains(handler.RequestedUrls, url =>
                url.Equals(ProviderUpdateService.DefaultManifestUrl, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(handler.RequestedUrls, url =>
                url.Equals(OfficialMainManifestUrl, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupTempDirectory(providersDirectory);
        }
    }

    [Fact]
    public async Task UpdateProvidersAsync_EmptyManifestBody_ReturnsFriendlyError()
    {
        const string manifestUrl = "https://example.com/providers/manifest.json";
        var settings = new InMemorySettingsStore();
        await settings.SetStringAsync(ProviderUpdateService.SettingsManifestUrlKey, manifestUrl);

        var handler = new StubHttpMessageHandler();
        handler.AddText(manifestUrl, "   ");

        using var http = new HttpClient(handler);
        var providersDirectory = CreateTempDirectory();

        try
        {
            var service = new ProviderUpdateService(
                settings,
                httpClient: http,
                providersDirectory: providersDirectory);

            var result = await service.UpdateProvidersAsync();

            Assert.Equal(0, result.DownloadedCount);
            Assert.Equal(0, result.SkippedCount);
            Assert.Contains("Manifesto vazio", result.Message);
            Assert.Contains(manifestUrl, result.Message);
        }
        finally
        {
            CleanupTempDirectory(providersDirectory);
        }
    }

    [Fact]
    public async Task UpdateProvidersAsync_InvalidJsonManifest_ReturnsFriendlyError()
    {
        const string manifestUrl = "https://example.com/providers/manifest.json";
        var settings = new InMemorySettingsStore();
        await settings.SetStringAsync(ProviderUpdateService.SettingsManifestUrlKey, manifestUrl);

        var handler = new StubHttpMessageHandler();
        handler.AddText(manifestUrl, "<html>not-json</html>");

        using var http = new HttpClient(handler);
        var providersDirectory = CreateTempDirectory();

        try
        {
            var service = new ProviderUpdateService(
                settings,
                httpClient: http,
                providersDirectory: providersDirectory);

            var result = await service.UpdateProvidersAsync();

            Assert.Equal(0, result.DownloadedCount);
            Assert.Equal(0, result.SkippedCount);
            Assert.Contains("Manifesto inválido (JSON)", result.Message);
            Assert.Contains(manifestUrl, result.Message);
        }
        finally
        {
            CleanupTempDirectory(providersDirectory);
        }
    }

    [Fact]
    public async Task UpdateProvidersAsync_DownloadsAssemblyAndPersistsInstalledVersion()
    {
        const string manifestUrl = "https://example.com/providers/manifest.json";
        const string assemblyUrl = "https://cdn.example/providers/MeuProvider.dll";

        var settings = new InMemorySettingsStore();
        await settings.SetStringAsync(ProviderUpdateService.SettingsManifestUrlKey, manifestUrl);

        var dllBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04 };
        var sha256 = Convert.ToHexString(SHA256.HashData(dllBytes));
        var manifest = $$"""
                         {
                           "providers": [
                             {
                               "name": "MeuProvider",
                               "version": "1.2.3",
                               "assemblyUrl": "{{assemblyUrl}}",
                               "sha256": "{{sha256}}",
                               "enabled": true
                             }
                           ]
                         }
                         """;

        var handler = new StubHttpMessageHandler();
        handler.AddJson(manifestUrl, manifest);
        handler.AddBytes(assemblyUrl, dllBytes);

        using var http = new HttpClient(handler);
        var providersDirectory = CreateTempDirectory();

        try
        {
            var service = new ProviderUpdateService(
                settings,
                httpClient: http,
                providersDirectory: providersDirectory);

            var result = await service.UpdateProvidersAsync();

            Assert.Equal(1, result.DownloadedCount);
            Assert.Equal(0, result.SkippedCount);

            var assemblyPath = Path.Combine(providersDirectory, "MeuProvider.dll");
            Assert.True(File.Exists(assemblyPath));

            var storedBytes = await File.ReadAllBytesAsync(assemblyPath);
            Assert.Equal(dllBytes, storedBytes);

            var versionKey = "Providers.DynamicUpdates.InstalledVersion.MeuProvider";
            var installedVersion = await settings.GetStringAsync(versionKey);
            Assert.Equal("1.2.3", installedVersion);
        }
        finally
        {
            CleanupTempDirectory(providersDirectory);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "NekoSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupTempDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<string?> GetStringAsync(string key, string? defaultValue = null)
        {
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : defaultValue);
        }

        public Task SetStringAsync(string key, string value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public async Task<int> GetIntAsync(string key, int defaultValue = 0)
        {
            var raw = await GetStringAsync(key);
            return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
        }

        public Task SetIntAsync(string key, int value)
        {
            return SetStringAsync(key, value.ToString());
        }

        public async Task<bool> GetBoolAsync(string key, bool defaultValue = false)
        {
            var raw = await GetStringAsync(key);
            return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
        }

        public Task SetBoolAsync(string key, bool value)
        {
            return SetStringAsync(key, value.ToString());
        }

        public async Task<T> GetEnumAsync<T>(string key, T defaultValue) where T : struct, Enum
        {
            var raw = await GetStringAsync(key);
            return Enum.TryParse<T>(raw, true, out var parsed) ? parsed : defaultValue;
        }

        public Task SetEnumAsync<T>(string key, T value) where T : struct, Enum
        {
            return SetStringAsync(key, value.ToString());
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<string> RequestedUrls { get; } = [];

        public void AddJson(string url, string payload, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses[url] = () => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }

        public void AddText(string url, string payload, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses[url] = () => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/plain")
            };
        }

        public void AddBytes(string url, byte[] payload, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses[url] = () => new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(payload)
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            RequestedUrls.Add(url);

            if (_responses.TryGetValue(url, out var responseFactory))
                return Task.FromResult(responseFactory());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("404: Not Found", Encoding.UTF8, "text/plain")
            });
        }
    }
}
