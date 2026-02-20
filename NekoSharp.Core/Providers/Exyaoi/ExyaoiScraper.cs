using System.Diagnostics;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Exyaoi;

public sealed class ExyaoiScraper : IScraper
{
    public string Name => "Exyaoi";
    public string BaseUrl => "https://3xyaoi.com";

    private readonly HttpClient _http;
    private readonly IBrowsingContext _browser;

    public ExyaoiScraper() : this(null, null) { }

    public ExyaoiScraper(LogService? logService) : this(logService, null) { }

    public ExyaoiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
    {
         
         
         
         
        HttpMessageHandler inner = new CloudflareHandler(
            inner: new HttpClientHandler(),
            logService: logService,
            store: cfStore);

        HttpMessageHandler handler = logService != null
            ? new LoggingHttpHandler(logService, inner)
            : inner;

        _http = new HttpClient(handler);
        _http.BaseAddress = new Uri("https://3xyaoi.com/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
        _http.DefaultRequestHeaders.Add("Referer", "https://3xyaoi.com/");
        _http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        _browser = BrowsingContext.New(Configuration.Default);
    }

    public bool CanHandle(string url)
    {
        return url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL invalida do Exyaoi.", nameof(url));

        var html = await _http.GetStringAsync(url, ct);
        var doc = await _browser.OpenAsync(req => req.Content(html), ct);

        var coverImg = doc.QuerySelector<IHtmlImageElement>("div.summary_image img");
        var coverUrl = coverImg?.Source ?? string.Empty;
        var titleFromAlt = coverImg?.AlternativeText ?? string.Empty;

        var titleNode = doc.QuerySelector("h1");
        var title = !string.IsNullOrWhiteSpace(titleNode?.TextContent)
            ? titleNode.TextContent.Trim()
            : titleFromAlt.Trim();

        var descriptionNode = doc.QuerySelector("div.summary__content p");
        var descritionText = descriptionNode!.TextContent.Trim();

        return new Manga
        {
            Name = title,
            CoverUrl = coverUrl,
            Url = url,
            Description = descritionText,
            SiteName = Name
        };
    }

    public async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var response = await _http.GetStringAsync(url, ct);
        var doc = await _browser.OpenAsync(req => req.Content(response), ct);
        
        var chapters = new List<Chapter>();
        var chapterList = doc.QuerySelectorAll("ul.main.version-chap.no-volumn li.wp-manga-chapter");

        foreach (var ch in chapterList)
        {
            var link = ch.QuerySelector("a");
            if (link == null)
                continue;

            var urlChapter = link.GetAttribute("href");
            var rawTitle = link.TextContent.Trim();

            if (string.IsNullOrWhiteSpace(url))
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

        var html = await _http.GetStringAsync(chapter.Url, ct);
        var doc = await _browser.OpenAsync(req => req.Content(html), ct);

        var imageNodes = doc.QuerySelectorAll("img.wp-manga-chapter-img");
        var pages = new List<Page>();

        var index = 1;
        foreach (var img in imageNodes)
        {
            var src = img.GetAttribute("src")?.Trim();
            if (string.IsNullOrWhiteSpace(src))
                continue;

            pages.Add(new Page
            {
                Number = index++,
                ImageUrl = src
            });
        }

        return pages;
    }
}
