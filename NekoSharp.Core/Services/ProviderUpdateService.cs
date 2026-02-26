using System.Security.Cryptography;
using System.Text.Json;

namespace NekoSharp.Core.Services;

public sealed record ProviderUpdateResult(
    int DownloadedCount,
    int SkippedCount,
    string Message,
    DateTimeOffset CheckedAtUtc);

public sealed class ProviderUpdateService
{
    public const string SettingsEnabledKey = "Providers.DynamicUpdates.Enabled";
    public const string SettingsManifestUrlKey = "Providers.DynamicUpdates.ManifestUrl";
    public const string SettingsLastCheckUtcKey = "Providers.DynamicUpdates.LastCheckUtc";

    // Manifesto oficial de providers externos no repositório principal do projeto.
    public const string DefaultManifestUrl = "https://raw.githubusercontent.com/YanEmmanuel/NekoSharp/main/providers/manifest.json";
    private const string LegacyManifestUrl = "https://raw.githubusercontent.com/yan/NekoSharp/main/providers/manifest.json";

    private const string InstalledVersionPrefix = "Providers.DynamicUpdates.InstalledVersion.";

    private readonly ISettingsStore _settingsStore;
    private readonly LogService? _log;
    private readonly HttpClient _httpClient;
    private readonly string _providersDirectory;

    public ProviderUpdateService(
        ISettingsStore settingsStore,
        LogService? logService = null,
        HttpClient? httpClient = null,
        string? providersDirectory = null)
    {
        _settingsStore = settingsStore;
        _log = logService;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);

        _providersDirectory = providersDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NekoSharp",
            "providers");

        Directory.CreateDirectory(_providersDirectory);
    }

    public IReadOnlyList<string> GetInstalledProviderAssemblies()
    {
        if (!Directory.Exists(_providersDirectory))
            return [];

        return Directory.GetFiles(_providersDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ProviderUpdateResult> UpdateProvidersAsync(CancellationToken ct = default)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        var isEnabled = await _settingsStore.GetBoolAsync(SettingsEnabledKey, true);
        if (!isEnabled)
        {
            await _settingsStore.SetStringAsync(SettingsLastCheckUtcKey, checkedAtUtc.ToString("O"));
            return new ProviderUpdateResult(0, 0, "Atualização dinâmica de providers está desativada.", checkedAtUtc);
        }

        var manifestUrl = await _settingsStore.GetStringAsync(SettingsManifestUrlKey, DefaultManifestUrl);
        if (string.Equals(manifestUrl, LegacyManifestUrl, StringComparison.OrdinalIgnoreCase))
        {
            manifestUrl = DefaultManifestUrl;
            await _settingsStore.SetStringAsync(SettingsManifestUrlKey, manifestUrl);
        }

        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            await _settingsStore.SetStringAsync(SettingsLastCheckUtcKey, checkedAtUtc.ToString("O"));
            return new ProviderUpdateResult(0, 0, "Manifesto de atualização de providers não configurado.", checkedAtUtc);
        }

        List<ProviderPackageManifest> packages;
        try
        {
            packages = await FetchManifestAsync(manifestUrl, ct);
        }
        catch (Exception ex)
        {
            _log?.Warn($"[ProviderUpdate] Falha ao carregar manifesto ({manifestUrl}): {ex.Message}");
            await _settingsStore.SetStringAsync(SettingsLastCheckUtcKey, checkedAtUtc.ToString("O"));
            return new ProviderUpdateResult(0, 0, $"Falha ao carregar manifesto de providers: {ex.Message}", checkedAtUtc);
        }

        if (packages.Count == 0)
        {
            await _settingsStore.SetStringAsync(SettingsLastCheckUtcKey, checkedAtUtc.ToString("O"));
            return new ProviderUpdateResult(0, 0, "Manifesto carregado, mas sem providers para atualizar.", checkedAtUtc);
        }

        var downloaded = 0;
        var skipped = 0;

        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();

            if (!package.Enabled)
            {
                skipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(package.Name) || string.IsNullOrWhiteSpace(package.AssemblyUrl))
            {
                _log?.Warn("[ProviderUpdate] Entrada de provider inválida no manifesto (name/url ausentes).");
                skipped++;
                continue;
            }

            var fileName = $"{SanitizeFileSegment(package.Name)}.dll";
            var targetPath = Path.Combine(_providersDirectory, fileName);
            var versionKey = InstalledVersionPrefix + SanitizeFileSegment(package.Name);
            var currentVersion = await _settingsStore.GetStringAsync(versionKey, string.Empty);

            if (File.Exists(targetPath) &&
                string.Equals(currentVersion, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            try
            {
                await DownloadAssemblyAsync(package, targetPath, ct);
                await _settingsStore.SetStringAsync(versionKey, package.Version);
                downloaded++;
                _log?.Info($"[ProviderUpdate] Provider atualizado: {package.Name} ({package.Version})");
            }
            catch (Exception ex)
            {
                skipped++;
                _log?.Warn($"[ProviderUpdate] Falha ao atualizar provider {package.Name}: {ex.Message}");
            }
        }

        await _settingsStore.SetStringAsync(SettingsLastCheckUtcKey, checkedAtUtc.ToString("O"));

        var message = downloaded > 0
            ? $"Atualização de providers concluída: {downloaded} baixado(s), {skipped} ignorado(s)."
            : "Nenhuma atualização de provider aplicada.";

        return new ProviderUpdateResult(downloaded, skipped, message, checkedAtUtc);
    }

    private async Task<List<ProviderPackageManifest>> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(content);

        if (!document.RootElement.TryGetProperty("providers", out var providersNode) ||
            providersNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var packages = new List<ProviderPackageManifest>();
        foreach (var item in providersNode.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetString(item, "name", "provider", "id") ?? string.Empty;
            var version = GetString(item, "version", "tag", "release") ?? "latest";
            var assemblyUrl = GetString(item, "assemblyUrl", "downloadUrl", "url") ?? string.Empty;
            var sha256 = GetString(item, "sha256", "checksum", "hash");
            var enabled = GetBool(item, "enabled") ?? true;

            packages.Add(new ProviderPackageManifest(name, version, assemblyUrl, sha256, enabled));
        }

        return packages;
    }

    private async Task DownloadAssemblyAsync(ProviderPackageManifest package, string targetPath, CancellationToken ct)
    {
        var tempPath = targetPath + ".tmp";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, package.AssemblyUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                await contentStream.CopyToAsync(fileStream, ct);
            }

            if (!string.IsNullOrWhiteSpace(package.Sha256))
            {
                var expectedHash = package.Sha256.Replace("-", string.Empty).Trim();
                await using var hashStream = File.OpenRead(tempPath);
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, ct));

                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Checksum SHA256 do provider não confere com o manifesto.");
            }

            File.Move(tempPath, targetPath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }

        return null;
    }

    private static bool? GetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.True)
                return true;

            if (value.ValueKind == JsonValueKind.False)
                return false;

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    private static string SanitizeFileSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "provider";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "provider" : sanitized;
    }

    private sealed record ProviderPackageManifest(
        string Name,
        string Version,
        string AssemblyUrl,
        string? Sha256,
        bool Enabled);
}
