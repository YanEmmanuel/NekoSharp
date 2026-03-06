using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.MediocreScan;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MediocreToons;

public sealed class MediocreToonsScraper : IScraper, ICredentialAuthProvider
{
    public string Name => "Mediocre Toons";
    public string BaseUrl => _inner.BaseUrl;

    private readonly MediocreScanScraper _inner;

    public MediocreToonsScraper() : this(null, null) { }

    public MediocreToonsScraper(LogService? logService) : this(logService, null) { }

    public MediocreToonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
    {
        _inner = new MediocreScanScraper(logService, cfStore);
    }

    public bool CanHandle(string url) => _inner.CanHandle(url);
    public Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default) => _inner.GetMangaInfoAsync(url, ct);
    public Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default) => _inner.GetChaptersAsync(url, ct);
    public Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default) => _inner.GetPagesAsync(chapter, ct);

    public Task<AuthSessionState> GetAuthStateAsync(CancellationToken ct = default) => _inner.GetAuthStateAsync(ct);
    public Task<AuthSessionState> LoginInteractivelyAsync(CancellationToken ct = default) => _inner.LoginInteractivelyAsync(ct);
    public Task<AuthSessionState> LoginWithCredentialsAsync(string usernameOrEmail, string password, bool rememberCredentials = true, CancellationToken ct = default)
        => _inner.LoginWithCredentialsAsync(usernameOrEmail, password, rememberCredentials, ct);
    public Task<bool> HasSavedCredentialsAsync(CancellationToken ct = default) => _inner.HasSavedCredentialsAsync(ct);
    public Task ClearSavedCredentialsAsync(CancellationToken ct = default) => _inner.ClearSavedCredentialsAsync(ct);
    public Task ClearAuthAsync(CancellationToken ct = default) => _inner.ClearAuthAsync(ct);
}
