using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;
using PuppeteerSharp;
using MangaPage = NekoSharp.Core.Models.Page;

namespace NekoSharp.Core.Providers.LittleTyrant;

public sealed class LittleTyrantScraper : HtmlScraperBase, ICredentialAuthProvider, IAuthenticatedRequestProvider
{
    private const string ProviderKey = "littletyrant";
    private static readonly Uri SiteRootUri = new("https://tiraninha.world/");
    private static readonly Uri LoginUri = new("https://tiraninha.world/login/");
    private static readonly Uri UserSettingsUri = new("https://tiraninha.world/user-settings/");
    private static readonly Uri AdminAjaxUri = new("https://tiraninha.world/wp-admin/admin-ajax.php");

    private static readonly Regex ReaderPagesRegex = new(
        @"var\s+pages\s*=\s*(\[[\s\S]*?\])\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // PuppeteerSharp 21.x crasha ao processar Runtime.consoleAPICalled de certos
    // args do site ("Specified cast is not valid"), fechando a página. Substituir
    // console.* por no-op antes dos scripts da página impede o evento CDP.
    private const string ConsoleSuppressionScript =
        """
        (() => {
            try {
                const noop = function() {};
                const methods = ["log","info","warn","error","debug","trace","dir","dirxml",
                    "table","group","groupCollapsed","groupEnd","count","countReset",
                    "time","timeLog","timeEnd","assert","profile","profileEnd","clear"];
                for (const name of methods) {
                    try { window.console[name] = noop; } catch (e) {}
                }
            } catch (e) {}
        })();
        """;

    private readonly ProviderCookieSessionStore _cookieStore;
    private readonly ProviderAuthStore _credentialStore;
    private readonly CloudflareCredentialStore? _cfStore;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly SemaphoreSlim _sessionLoadLock = new(1, 1);

    private CookieContainer _cookieContainer;
    private HttpClient _authHttp;
    private bool _sessionLoaded;
    private bool _sessionValidated;

    public override string Name => "Little Tyrant";

    public LittleTyrantScraper() : this(null, null) { }

    public LittleTyrantScraper(LogService? logService) : this(logService, null) { }

    public LittleTyrantScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://tiraninha.world", logService, cfStore)
    {
        _cfStore = cfStore;
        _cookieStore = new ProviderCookieSessionStore(logService: logService);
        _credentialStore = new ProviderAuthStore(logService: logService);
        (_cookieContainer, _authHttp) = CreateSessionHttpClient(logService, cfStore);
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentWithAuthFallbackAsync(url, ct);

        var coverUrl = ExtractImageSource(document.QuerySelector(".summary_image img, .detail-bg img"), url) ?? string.Empty;
        var title = document.QuerySelector("h1.manga-title-premium, h1.manga-title, h1")?.TextContent.Trim() ?? string.Empty;
        var description = document.QuerySelector(".mc-description-box p, .summary-text-scroll p, .summary__content p")?.TextContent.Trim() ??
                          document.QuerySelector("meta[name='description']")?.GetAttribute("content")?.Trim() ??
                          string.Empty;

        return new Manga
        {
            Name = title,
            CoverUrl = coverUrl,
            Description = description,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentWithAuthFallbackAsync(url, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chapters = ParseChapterLinks(
            document.QuerySelectorAll("li.wp-manga-chapter a.mc-chapter-link, li.wp-manga-chapter > a"),
            url,
            seen);

        var loadMoreButton = document.QuerySelector("#btn-load-more-chapters");
        if (loadMoreButton is not null &&
            int.TryParse(loadMoreButton.GetAttribute("data-manga-id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mangaId))
        {
            var offset = ParseOffset(loadMoreButton.GetAttribute("data-offset")) ?? chapters.Count;

            while (true)
            {
                var payload = await LoadMoreChaptersAsync(mangaId, offset, ct);
                if (payload is null || string.IsNullOrWhiteSpace(payload.Value.Html))
                    break;

                var fragment = await OpenDocumentAsync($"<ul>{payload.Value.Html}</ul>", url, ct);
                var newChapters = ParseChapterLinks(
                    fragment.QuerySelectorAll("li.wp-manga-chapter a.mc-chapter-link, li.wp-manga-chapter > a"),
                    url,
                    seen);

                if (newChapters.Count == 0)
                    break;

                chapters.AddRange(newChapters);

                if (!payload.Value.HasMore)
                    break;

                offset = payload.Value.NewOffset ?? (offset + newChapters.Count);
            }
        }

        return chapters
            .OrderByDescending(chapter => chapter.Number)
            .ThenByDescending(chapter => chapter.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public override async Task<List<MangaPage>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        if (string.IsNullOrWhiteSpace(chapter.Url))
            return [];

        var document = await LoadDocumentWithAuthFallbackAsync(chapter.Url, ct);
        var readerScripts = GetReaderScriptContents(document);
        var pages = ExtractPagesFromReaderScripts(
            readerScripts,
            chapter.Url);

        if (pages.Count > 0)
        {
            Log?.Debug($"[{Name}] Extraídas {pages.Count} páginas do leitor protegido para {chapter.Url}");
            return pages;
        }

        pages = ExtractPagesFromReaderScripts(
            document.QuerySelectorAll("script").Select(script => script.TextContent),
            chapter.Url);

        if (pages.Count > 0)
        {
            Log?.Debug($"[{Name}] Extraídas {pages.Count} páginas dos scripts da página para {chapter.Url}");
            return pages;
        }

        pages = CreatePagesFromNodes(
            document.QuerySelectorAll(".reading-content img, .page-break img, .manga-reading-content img"),
            chapter.Url);

        if (pages.Count > 0)
        {
            Log?.Debug($"[{Name}] Extraídas {pages.Count} páginas de elementos img para {chapter.Url}");
            return pages;
        }

        Log?.Debug($"[{Name}] HTTP não expôs páginas para {chapter.Url}. Tentando fallback com browser...");
        pages = await ExtractPagesWithBrowserFallbackAsync(chapter.Url, ct);

        if (pages.Count > 0)
        {
            Log?.Debug($"[{Name}] Extraídas {pages.Count} páginas via browser para {chapter.Url}");
            return pages;
        }

        throw new InvalidOperationException(
            "Little Tyrant não expôs imagens no capítulo. A sessão pode estar expirada; refaça o login do provider.");
    }

    public async Task<AuthSessionState> GetAuthStateAsync(CancellationToken ct = default)
    {
        await EnsureStoredSessionLoadedAsync(ct);
        var session = await _cookieStore.TryGetAsync(ProviderKey, ct);
        var isAuthenticated = session is not null && HasWordPressLoginCookie(session.Cookies);

        return new AuthSessionState
        {
            IsAuthenticated = isAuthenticated,
            IsExpired = false,
            ObtainedAtUtc = session?.UpdatedAtUtc,
            Message = isAuthenticated ? "Conectado" : "Desconectado",
            UserDisplayName = session?.UserDisplayName
        };
    }

    public async Task<AuthSessionState> LoginInteractivelyAsync(CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            await LoginInteractivelyInternalAsync(ct);
            _sessionValidated = true;
        }
        finally
        {
            _authLock.Release();
        }

        return await GetAuthStateAsync(ct);
    }

    public async Task<AuthSessionState> LoginWithCredentialsAsync(
        string usernameOrEmail,
        string password,
        bool rememberCredentials = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail))
            throw new ArgumentException("Usuário/email é obrigatório.", nameof(usernameOrEmail));

        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Senha é obrigatória.", nameof(password));

        await _authLock.WaitAsync(ct);
        try
        {
            await LoginWithCredentialsInternalAsync(usernameOrEmail.Trim(), password, rememberCredentials, ct);
            _sessionValidated = true;
        }
        finally
        {
            _authLock.Release();
        }

        return await GetAuthStateAsync(ct);
    }

    public async Task<bool> HasSavedCredentialsAsync(CancellationToken ct = default)
        => await _credentialStore.TryGetLoginSecretAsync(ProviderKey, ct) is not null;

    public Task ClearSavedCredentialsAsync(CancellationToken ct = default)
        => _credentialStore.RemoveLoginSecretAsync(ProviderKey, ct);

    public async Task ClearAuthAsync(CancellationToken ct = default)
    {
        await _cookieStore.RemoveAsync(ProviderKey, ct);
        await _credentialStore.RemoveLoginSecretAsync(ProviderKey, ct);
        ResetSessionHttpClient();
    }

    public async Task ApplyRequestAuthenticationAsync(
        HttpRequestMessage request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await EnsureAuthenticatedAsync(ct);
        ApplySessionCookies(request);
    }

    internal static List<MangaPage> ExtractPagesFromReaderScripts(IEnumerable<string?> scripts, string refererUrl)
    {
        var combined = string.Join("\n", scripts.Where(static script => !string.IsNullOrWhiteSpace(script)));
        var match = ReaderPagesRegex.Match(combined);
        if (!match.Success)
            return [];

        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        var pages = new List<MangaPage>();
        var index = 1;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var encoded = item.GetString();
            if (string.IsNullOrWhiteSpace(encoded))
                continue;

            try
            {
                var url = DecodeBase64Url(encoded);
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                pages.Add(new MangaPage
                {
                    Number = index++,
                    ImageUrl = url,
                    RefererUrl = refererUrl
                });
            }
            catch (FormatException)
            {
            }
        }

        return pages;
    }

    internal static (string Html, bool HasMore, int? NewOffset)? ParseLoadMorePayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            return null;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        var html = data.TryGetProperty("html", out var htmlValue) && htmlValue.ValueKind == JsonValueKind.String
            ? htmlValue.GetString() ?? string.Empty
            : string.Empty;

        var hasMore = data.TryGetProperty("has_more", out var hasMoreValue) &&
                      hasMoreValue.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                      hasMoreValue.GetBoolean();

        int? newOffset = data.TryGetProperty("new_offset", out var newOffsetValue) &&
                         newOffsetValue.TryGetInt32(out var parsedOffset)
            ? parsedOffset
            : null;

        return (html, hasMore, newOffset);
    }

    internal static string MergeCookieHeader(
        string? existingHeader,
        IReadOnlyDictionary<string, string> incomingCookies)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(existingHeader))
        {
            foreach (var segment in existingHeader.Split(
                         ';',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = segment.IndexOf('=');
                if (separatorIndex <= 0)
                    continue;

                var name = segment[..separatorIndex].Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                merged[name] = segment[(separatorIndex + 1)..].Trim();
            }
        }

        foreach (var cookie in incomingCookies)
        {
            if (!string.IsNullOrWhiteSpace(cookie.Key))
                merged[cookie.Key.Trim()] = cookie.Value ?? string.Empty;
        }

        return string.Join("; ", merged.Select(static cookie => $"{cookie.Key}={cookie.Value}"));
    }

    private static string DecodeBase64Url(string encoded)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim();
    }

    private async Task<IDocument> LoadDocumentWithAuthFallbackAsync(string url, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);

        var (document, finalUrl) = await FetchDocumentAsync(url, ct);
        if (!IsLoginDocument(document, finalUrl))
            return document;

        _sessionValidated = false;
        await EnsureAuthenticatedAsync(ct);

        (document, finalUrl) = await FetchDocumentAsync(url, ct);
        if (IsLoginDocument(document, finalUrl))
            throw new InvalidOperationException("Little Tyrant ainda exige login após tentativa de autenticação.");

        return document;
    }

    private async Task<(IDocument Document, string FinalUrl)> FetchDocumentAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySessionCookies(request);

        using var response = await _authHttp.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        var document = await OpenDocumentAsync(html, finalUrl, ct);
        return (document, finalUrl);
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            await EnsureStoredSessionLoadedAsync(ct);
            if (_sessionValidated && HasWordPressLoginCookie(CaptureCurrentCookies()))
                return;

            if (await IsCurrentSessionValidAsync(ct))
            {
                _sessionValidated = true;
                return;
            }

            await LoginInteractivelyInternalAsync(ct);
            _sessionValidated = true;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<bool> IsCurrentSessionValidAsync(CancellationToken ct)
    {
        var cookies = CaptureCurrentCookies();
        if (!HasWordPressLoginCookie(cookies))
            return false;

        var (document, finalUrl) = await FetchDocumentAsync(UserSettingsUri.ToString(), ct);
        if (IsLoginDocument(document, finalUrl))
            return false;

        await PersistCurrentSessionAsync(null, ct);
        return true;
    }

    private async Task LoginWithCredentialsInternalAsync(
        string usernameOrEmail,
        string password,
        bool rememberCredentials,
        CancellationToken ct)
    {
        ResetSessionHttpClient();

        var (loginDocument, _) = await FetchDocumentAsync(LoginUri.ToString(), ct);
        var nonce = loginDocument.QuerySelector("input[name='littletyrant_login_nonce']")?.GetAttribute("value");
        if (string.IsNullOrWhiteSpace(nonce))
            throw new InvalidOperationException("Little Tyrant não expôs nonce de login.");

        var referer = loginDocument.QuerySelector("input[name='_wp_http_referer']")?.GetAttribute("value")?.Trim();
        if (string.IsNullOrWhiteSpace(referer))
            referer = "/login/";

        using var request = new HttpRequestMessage(HttpMethod.Post, LoginUri)
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("littletyrant_login_nonce", nonce),
                new KeyValuePair<string, string>("_wp_http_referer", referer),
                new KeyValuePair<string, string>("log", usernameOrEmail),
                new KeyValuePair<string, string>("pwd", password),
                new KeyValuePair<string, string>("rememberme", rememberCredentials ? "forever" : string.Empty),
                new KeyValuePair<string, string>("submit_login", "Entrar")
            ])
        };

        request.Headers.Referrer = LoginUri;
        request.Headers.TryAddWithoutValidation("Origin", SiteRootUri.GetLeftPart(UriPartial.Authority));
        ApplySessionCookies(request);

        using var response = await _authHttp.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        if (!HasWordPressLoginCookie(CaptureCurrentCookies()))
        {
            var html = await response.Content.ReadAsStringAsync(ct);
            var document = await OpenDocumentAsync(html, response.RequestMessage?.RequestUri?.ToString() ?? LoginUri.ToString(), ct);
            var error = document.QuerySelector(".alert-error, .woocommerce-error, .login-error, #login_error")?.TextContent.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Login do Little Tyrant falhou." : error);
        }

        if (rememberCredentials)
            await _credentialStore.SaveLoginSecretAsync(ProviderKey, usernameOrEmail, password, ct);

        await PersistCurrentSessionAsync(null, ct);
    }

    private async Task LoginInteractivelyInternalAsync(CancellationToken ct)
    {
        IBrowser? browser = null;
        try
        {
            browser = await LaunchBrowserAsync(ct);
            await using var page = await browser.NewPageAsync();
            await page.EvaluateExpressionOnNewDocumentAsync(ConsoleSuppressionScript);

            await page.GoToAsync(LoginUri.ToString(), new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = (int)TimeSpan.FromSeconds(60).TotalMilliseconds
            });

            var timeoutAt = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < timeoutAt)
            {
                ct.ThrowIfCancellationRequested();

                var cookies = await page.GetCookiesAsync();
                var mapped = cookies
                    .Where(static cookie => cookie.Domain.Contains("tiraninha.world", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

                if (HasWordPressLoginCookie(mapped))
                {
                    var userAgent = await page.EvaluateExpressionAsync<string>("navigator.userAgent");
                    ResetSessionHttpClient();
                    ApplyCookies(mapped);
                    await PersistCurrentSessionAsync(string.IsNullOrWhiteSpace(userAgent) ? null : userAgent, ct);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            throw new TimeoutException("Tempo esgotado aguardando login do Little Tyrant no navegador.");
        }
        finally
        {
            if (browser is not null)
            {
                try { await browser.CloseAsync(); } catch { }
            }
        }
    }

    private static async Task<IBrowser> LaunchBrowserAsync(CancellationToken ct)
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
        catch
        {
            var fetcher = new BrowserFetcher();
            var installed = await fetcher.DownloadAsync();
            options.ExecutablePath = fetcher.GetExecutablePath(installed.BuildId);

            ct.ThrowIfCancellationRequested();
            return await Puppeteer.LaunchAsync(options);
        }
    }

    private static async Task<IBrowser> LaunchHeadlessBrowserAsync(CancellationToken ct)
    {
        var options = new LaunchOptions
        {
            Headless = true,
            DefaultViewport = null,
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-blink-features=AutomationControlled"]
        };

        try
        {
            ct.ThrowIfCancellationRequested();
            return await Puppeteer.LaunchAsync(options);
        }
        catch
        {
            var fetcher = new BrowserFetcher();
            var installed = await fetcher.DownloadAsync();
            options.ExecutablePath = fetcher.GetExecutablePath(installed.BuildId);

            ct.ThrowIfCancellationRequested();
            return await Puppeteer.LaunchAsync(options);
        }
    }

    private async Task<List<MangaPage>> ExtractPagesWithBrowserFallbackAsync(string chapterUrl, CancellationToken ct)
    {
        IBrowser? browser = null;
        try
        {
            browser = await LaunchHeadlessBrowserAsync(ct);
            await using var page = await browser.NewPageAsync();
            await page.EvaluateExpressionOnNewDocumentAsync(ConsoleSuppressionScript);

            var cookies = CaptureCurrentCookies();
            if (cookies.Count > 0)
            {
                var origin = SiteRootUri.GetLeftPart(UriPartial.Authority);
                await page.SetCookieAsync(cookies
                    .Where(static kv => !string.IsNullOrWhiteSpace(kv.Key))
                    .Select(kv => new CookieParam
                    {
                        Name = kv.Key,
                        Value = kv.Value,
                        Url = origin,
                        Path = "/",
                        Secure = true
                    }).ToArray());
            }

            var userAgent = _authHttp.DefaultRequestHeaders.UserAgent.ToString();
            if (!string.IsNullOrWhiteSpace(userAgent))
                await page.SetUserAgentAsync(userAgent);

            await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
            {
                ["Referer"] = SiteRootUri.ToString(),
                ["Accept-Language"] = "en-US,en;q=0.9"
            });

            var navOptions = new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = (int)TimeSpan.FromSeconds(45).TotalMilliseconds
            };

            try
            {
                await page.GoToAsync(chapterUrl, navOptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Frame detach (bug de console do PuppeteerSharp / redirect). Tenta novamente.
                Log?.Debug($"[{Name}] Navegação falhou ({ex.GetType().Name}: {ex.Message}). Tentando novamente.");
                await page.GoToAsync(chapterUrl, navOptions);
            }

            try
            {
                await page.WaitForSelectorAsync("#secure-manga-reader", new WaitForSelectorOptions
                {
                    Timeout = (int)TimeSpan.FromSeconds(15).TotalMilliseconds
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reader container ausente; tenta extrair do conteúdo atual mesmo assim.
                Log?.Debug($"[{Name}] Reader não encontrado ({ex.GetType().Name}). Extraindo conteúdo atual.");
            }

            var html = await page.GetContentAsync();
            if (string.IsNullOrWhiteSpace(html))
                return [];

            var document = await OpenDocumentAsync(html, chapterUrl, ct);

            var readerScripts = GetReaderScriptContents(document);
            var pages = ExtractPagesFromReaderScripts(readerScripts, chapterUrl);
            if (pages.Count > 0)
                return pages;

            return ExtractPagesFromReaderScripts(
                document.QuerySelectorAll("script").Select(static s => s.TextContent),
                chapterUrl);
        }
        finally
        {
            if (browser is not null)
                try { await browser.CloseAsync(); } catch { }
        }
    }

    private async Task EnsureStoredSessionLoadedAsync(CancellationToken ct)
    {
        if (_sessionLoaded)
            return;

        await _sessionLoadLock.WaitAsync(ct);
        try
        {
            if (_sessionLoaded)
                return;

            var session = await _cookieStore.TryGetAsync(ProviderKey, ct);
            if (session is not null)
            {
                ApplyCookies(session.Cookies);
                if (!string.IsNullOrWhiteSpace(session.UserAgent))
                    SetUserAgent(session.UserAgent);
            }

            _sessionLoaded = true;
        }
        finally
        {
            _sessionLoadLock.Release();
        }
    }

    private async Task PersistCurrentSessionAsync(string? userAgent, CancellationToken ct)
    {
        var cookies = CaptureCurrentCookies();
        if (cookies.Count == 0)
            return;

        var effectiveUserAgent = string.IsNullOrWhiteSpace(userAgent)
            ? _authHttp.DefaultRequestHeaders.UserAgent.ToString()
            : userAgent!;

        if (!string.IsNullOrWhiteSpace(userAgent))
            SetUserAgent(userAgent);

        await _cookieStore.SaveAsync(new ProviderCookieSession
        {
            ProviderKey = ProviderKey,
            BaseUrl = BaseUrl,
            UserAgent = string.IsNullOrWhiteSpace(effectiveUserAgent) ? UserAgentProvider.Default : effectiveUserAgent,
            Cookies = cookies,
            UpdatedAtUtc = DateTime.UtcNow,
            UserDisplayName = ExtractUserDisplayName(cookies)
        }, ct);

        _sessionLoaded = true;
    }

    private List<Chapter> ParseChapterLinks(IEnumerable<IElement> nodes, string refererUrl, ISet<string> seen)
    {
        var chapters = new List<Chapter>();

        foreach (var node in nodes)
        {
            var url = ToAbsoluteUrl(refererUrl, node.GetAttribute("href"));
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                continue;

            var title = node.QuerySelector(".mc-chapter-title")?.TextContent.Trim() ??
                        node.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var chapterNumber = ChapterHelper.ExtractChapterNumber(title);
            var normalizedTitle = chapterNumber > 0
                ? $"Capítulo {chapterNumber.ToString(chapterNumber % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)}"
                : title;

            chapters.Add(new Chapter
            {
                Title = normalizedTitle,
                Number = chapterNumber,
                Url = url
            });
        }

        return chapters;
    }

    private async Task<(string Html, bool HasMore, int? NewOffset)?> LoadMoreChaptersAsync(int mangaId, int offset, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AdminAjaxUri)
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("action", "load_more_chapters"),
                new KeyValuePair<string, string>("manga_id", mangaId.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("offset", offset.ToString(CultureInfo.InvariantCulture))
            ])
        };

        request.Headers.Referrer = new Uri(BaseUrl);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        ApplySessionCookies(request);

        using var response = await _authHttp.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseLoadMorePayload(json);
    }

    private static int? ParseOffset(string? rawValue)
    {
        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static IEnumerable<string?> GetReaderScriptContents(IDocument document)
    {
        var reader = document.QuerySelector("#secure-manga-reader");
        if (reader is null)
            return [];

        var scripts = new List<string?>();
        for (var sibling = reader.NextElementSibling; sibling is not null; sibling = sibling.NextElementSibling)
        {
            if (!sibling.TagName.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase))
                continue;

            scripts.Add(sibling.TextContent);

            if (sibling.TextContent.Contains("var _HuntersOpts", StringComparison.Ordinal))
                break;
        }

        return scripts;
    }

    private static bool IsLoginDocument(IDocument document, string finalUrl)
    {
        if (Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.Trim('/').Equals("login", StringComparison.OrdinalIgnoreCase))
            return true;

        return document.QuerySelector("input[name='littletyrant_login_nonce'], form#loginform input[name='log'], button[name='submit_login']") is not null;
    }

    private static bool HasWordPressLoginCookie(IReadOnlyDictionary<string, string> cookies)
    {
        return cookies.Keys.Any(static key =>
            key.StartsWith("wordpress_logged_in_", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractUserDisplayName(IReadOnlyDictionary<string, string> cookies)
    {
        var pair = cookies.FirstOrDefault(static entry =>
            entry.Key.StartsWith("wordpress_logged_in_", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(pair.Value))
            return null;

        return pair.Value.Split('|', 2, StringSplitOptions.None)[0].Trim();
    }

    private void ResetSessionHttpClient()
    {
        try
        {
            _authHttp.Dispose();
        }
        catch
        {
        }

        (_cookieContainer, _authHttp) = CreateSessionHttpClient(Log, _cfStore);
        _sessionLoaded = false;
        _sessionValidated = false;
    }

    private (CookieContainer Cookies, HttpClient Client) CreateSessionHttpClient(LogService? logService, CloudflareCredentialStore? cfStore)
    {
        var cookieContainer = new CookieContainer();
        var inner = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli,
            CookieContainer = cookieContainer,
            UseCookies = true,
        };

        HttpMessageHandler handler = new CloudflareHandler(
            inner: inner,
            logService: logService,
            store: cfStore);

        if (logService is not null)
            handler = new LoggingHttpHandler(logService, handler);

        var client = new HttpClient(handler)
        {
            BaseAddress = SiteRootUri,
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", SiteRootUri.ToString());
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        return (cookieContainer, client);
    }

    private void ApplyCookies(IReadOnlyDictionary<string, string> cookies)
    {
        foreach (var cookie in cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Key))
                continue;

            _cookieContainer.Add(SiteRootUri, new Cookie(cookie.Key, cookie.Value, "/", SiteRootUri.Host));
        }
    }

    private void ApplySessionCookies(HttpRequestMessage request)
    {
        var cookies = CaptureCurrentCookies();
        if (cookies.Count > 0)
        {
            var existingCookies = request.Headers.TryGetValues("Cookie", out var values)
                ? string.Join("; ", values)
                : null;
            var cookieHeader = MergeCookieHeader(existingCookies, cookies);

            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        var userAgent = _authHttp.DefaultRequestHeaders.UserAgent.ToString();
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
    }

    private Dictionary<string, string> CaptureCurrentCookies()
    {
        return _cookieContainer
            .GetCookies(SiteRootUri)
            .Cast<Cookie>()
            .Where(static cookie => !cookie.Expired)
            .ToDictionary(cookie => cookie.Name, cookie => cookie.Value, StringComparer.OrdinalIgnoreCase);
    }

    private void SetUserAgent(string userAgent)
    {
        _authHttp.DefaultRequestHeaders.UserAgent.Clear();
        _authHttp.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }
}
