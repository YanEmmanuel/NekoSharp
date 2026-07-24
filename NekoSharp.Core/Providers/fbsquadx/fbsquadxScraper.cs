using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.fbsquadx;

public sealed class fbsquadxScraper : IScraper
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _nextRequestUtc = DateTimeOffset.MinValue;

    public string Name => "fbsquadx";
    public string BaseUrl => "https://fbsquadx.com";

    private readonly HttpClient _http;
    private readonly IBrowsingContext _browser;
    private readonly LogService? _log;
    private readonly bool _rateLimit;

    public fbsquadxScraper() : this(null, null, null) { }
    public fbsquadxScraper(LogService? logService) : this(logService, null, null) { }
    public fbsquadxScraper(LogService? logService, CloudflareCredentialStore? cfStore) : this(logService, cfStore, null) { }

    public fbsquadxScraper(IBrowsingContext browser) : this(null, null, browser) { }
    public fbsquadxScraper(LogService? logService, IBrowsingContext browser) : this(logService, null, browser) { }

    internal fbsquadxScraper(HttpMessageHandler handler)
        : this(null, null, null, handler, rateLimit: false) { }

    private fbsquadxScraper(LogService? logService, CloudflareCredentialStore? cfStore, IBrowsingContext? browser)
        : this(logService, cfStore, browser, null, rateLimit: true) { }

    private fbsquadxScraper(
        LogService? logService,
        CloudflareCredentialStore? cfStore,
        IBrowsingContext? browser,
        HttpMessageHandler? handler,
        bool rateLimit)
    {
        _log = logService;
        _browser = browser ?? BrowsingContext.New(Configuration.Default);
        _rateLimit = rateLimit;

        if (handler is null)
        {
            HttpMessageHandler inner = new CloudflareHandler(
                inner: new HttpClientHandler(),
                logService: logService,
                store: cfStore);

            handler = logService != null
                ? new LoggingHttpHandler(logService, inner)
                : inner;
        }

        _http = new HttpClient(handler);
        _http.BaseAddress = new Uri("https://fbsquadx.com/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
        _http.DefaultRequestHeaders.Add("Referer", "https://fbsquadx.com/");
        _http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    public bool CanHandle(string url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL invalida do fbsquadx.", nameof(url));

        var html = await GetHtmlAsync(url, ct);
        var doc = await _browser.OpenAsync(req => req.Content(html).Address(url), ct);

        var coverImg = doc.QuerySelector<IHtmlImageElement>("div.summary_image img");
        var coverUrl = coverImg?.Source ?? string.Empty;
        var titleFromAlt = coverImg?.AlternativeText ?? string.Empty;

        var titleNode = doc.QuerySelector("div.summary__content h1")
                        ?? doc.QuerySelector("h1");
        var title = !string.IsNullOrWhiteSpace(titleNode?.TextContent)
            ? titleNode.TextContent.Trim()
            : titleFromAlt.Trim();

        var descriptionText =
            doc.QuerySelector("div.manga-about.manga-info p")?.TextContent.Trim() ??
            doc.QuerySelector("div.manga-summary p")?.TextContent.Trim() ??
            doc.QuerySelector("div.summary__content p")?.TextContent.Trim() ??
            string.Empty;

        return new Manga
        {
            Name = title,
            CoverUrl = coverUrl,
            Url = url,
            Description = descriptionText,
            SiteName = Name
        };
    }

    public async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var response = await GetHtmlAsync(url, ct);
        var doc = await _browser.OpenAsync(req => req.Content(response).Address(url), ct);

        var chapterLinks = doc.QuerySelectorAll("li.wp-manga-chapter > a, li.wp-manga-chapter a")
            .OfType<IElement>()
            .DistinctBy(link => link.GetAttribute("href") ?? string.Empty)
            .ToList();

        if (chapterLinks.Count == 0 && doc.QuerySelector("div[id^='manga-chapters-holder']") is not null)
        {
            var endpoint = $"{url.TrimEnd('/')}/ajax/chapters";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Referrer = new Uri(url);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            response = await SendForHtmlAsync(request, ct);
            doc = await _browser.OpenAsync(req => req.Content(response).Address(endpoint), ct);
            chapterLinks = doc.QuerySelectorAll("li.wp-manga-chapter > a, li.wp-manga-chapter a")
                .OfType<IElement>()
                .DistinctBy(link => link.GetAttribute("href") ?? string.Empty)
                .ToList();
        }

        _log?.Debug($"[{Name}] Chapter selector matched {chapterLinks.Count} nodes for {url}");

        var chapters = new List<Chapter>(chapterLinks.Count);

        foreach (var link in chapterLinks)
        {
            var urlChapter = link.GetAttribute("href");
            var rawTitle = link.TextContent.Trim();

            if (string.IsNullOrWhiteSpace(urlChapter))
                continue;

            if (string.IsNullOrWhiteSpace(rawTitle))
                continue;

            if (Uri.TryCreate(urlChapter, UriKind.Relative, out var relative))
                urlChapter = new Uri(_http.BaseAddress!, relative).ToString();

            if (!urlChapter.Contains("style=list", StringComparison.OrdinalIgnoreCase))
                urlChapter += urlChapter.Contains('?') ? "&style=list" : "?style=list";

            var title = rawTitle.Contains(" - ")
                ? rawTitle.Split(" - ", 2)[1].Trim()
                : rawTitle;

            chapters.Add(new Chapter
            {
                Number = ChapterHelper.ExtractChapterNumber(rawTitle),
                Title = title,
                Url = urlChapter
            });
        }

        _log?.Debug($"[{Name}] Parsed {chapters.Count} chapters for {url}");

        return chapters;
    }

    public async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        if (chapter == null)
            throw new ArgumentNullException(nameof(chapter));

        if (string.IsNullOrWhiteSpace(chapter.Url))
            return new List<Page>();

        using var req = new HttpRequestMessage(HttpMethod.Get, chapter.Url);
        req.Headers.Referrer = new Uri(chapter.Url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

        var html = await SendForHtmlAsync(req, ct);

        var doc = await _browser.OpenAsync(r => r.Content(html).Address(chapter.Url), ct);

        var imageNodes = doc.QuerySelectorAll(".reading-content img.wp-manga-chapter-img");

        var pages = new List<Page>(imageNodes.Length);

        var index = 1;
        foreach (var img in imageNodes)
        {
            var src =
                img.GetAttribute("data-src") ??
                img.GetAttribute("data-lazy") ??
                img.GetAttribute("src");

            src = src?.Trim();

            if (string.IsNullOrWhiteSpace(src))
                continue;

            if (Uri.TryCreate(src, UriKind.Relative, out var rel) && _http.BaseAddress != null)
                src = new Uri(_http.BaseAddress, rel).ToString();

            pages.Add(new Page
            {
                Number = index++,
                ImageUrl = src,
                RefererUrl = chapter.Url
            });
        }

        return pages;
    }

    private async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendForHtmlAsync(request, ct);
    }

    private async Task<string> SendForHtmlAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_rateLimit)
            await WaitForRequestSlotAsync(ct);

        using var response = await _http.SendAsync(request, ct);

        if (response.RequestMessage?.RequestUri?.AbsolutePath.Contains("wp-login.php", StringComparison.OrdinalIgnoreCase) == true)
            throw new IOException("É necessário realizar o login via WebView para acessar a fonte.");

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task WaitForRequestSlotAsync(CancellationToken ct)
    {
        await RequestGate.WaitAsync(ct);
        try
        {
            var delay = _nextRequestUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            _nextRequestUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        }
        finally
        {
            RequestGate.Release();
        }
    }
}
