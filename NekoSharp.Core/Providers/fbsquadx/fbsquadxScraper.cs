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
    public string Name => "fbsquadx";
    public string BaseUrl => "https://fbsquadx.com";

    private readonly HttpClient _http;
    private readonly IBrowsingContext _browser;

    public fbsquadxScraper() : this(null, null, null) { }
    public fbsquadxScraper(LogService? logService) : this(logService, null, null) { }
    public fbsquadxScraper(LogService? logService, CloudflareCredentialStore? cfStore) : this(logService, cfStore, null) { }

    public fbsquadxScraper(IBrowsingContext browser) : this(null, null, browser) { }
    public fbsquadxScraper(LogService? logService, IBrowsingContext browser) : this(logService, null, browser) { }

    private fbsquadxScraper(LogService? logService, CloudflareCredentialStore? cfStore, IBrowsingContext? browser)
    {
        _browser = browser ?? BrowsingContext.New(Configuration.Default);

        HttpMessageHandler inner = new CloudflareHandler(
            inner: new HttpClientHandler(),
            logService: logService,
            store: cfStore);

        HttpMessageHandler handler = logService != null
            ? new LoggingHttpHandler(logService, inner)
            : inner;

        _http = new HttpClient(handler);
        _http.BaseAddress = new Uri("https://fbsquadx.com/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
        _http.DefaultRequestHeaders.Add("Referer", "https://fbsquadx.com/");
        _http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    public bool CanHandle(string url)
    {
        return url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL invalida do fbsquadx.", nameof(url));

        var html = await _http.GetStringAsync(url, ct);
        var doc = await _browser.OpenAsync(req => req.Content(html), ct);

        var coverImg = doc.QuerySelector<IHtmlImageElement>("div.summary_image img");
        var coverUrl = coverImg?.Source ?? string.Empty;
        var titleFromAlt = coverImg?.AlternativeText ?? string.Empty;

        var titleNode = doc.QuerySelector("div.summary__content h1")
                        ?? doc.QuerySelector("h1");
        var title = !string.IsNullOrWhiteSpace(titleNode?.TextContent)
            ? titleNode.TextContent.Trim()
            : titleFromAlt.Trim();

        var descriptionNode = doc.QuerySelector("div.manga-about.manga-info p");
        var descriptionText = descriptionNode?.TextContent.Trim() ?? string.Empty;

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
        var response = await _http.GetStringAsync(url, ct);
        var doc = await _browser.OpenAsync(req => req.Content(response), ct);

        var chapters = new List<Chapter>();

        var chapterLinks = doc.QuerySelectorAll("ul.sub-chap-list li.wp-manga-chapter > a");

        foreach (var link in chapterLinks)
        {
            var urlChapter = link.GetAttribute("href");
            var rawTitle = link.TextContent.Trim();

            if (string.IsNullOrWhiteSpace(urlChapter))
                continue;

            if (string.IsNullOrWhiteSpace(rawTitle))
                continue;

             
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

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var html = await res.Content.ReadAsStringAsync(ct);

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
                ImageUrl = src
            });
        }

        return pages;
    }
}
