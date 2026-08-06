using System.Globalization;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.FlowerMangaDotNet;

public sealed class FlowerMangaDotNetScraper : IScraper
{
    public string Name => "FlowerManga.net";
    public string BaseUrl => "https://flowermanga.org";

    private readonly HttpClient _http;
    private readonly IBrowsingContext _browser = BrowsingContext.New(Configuration.Default);

    public FlowerMangaDotNetScraper() : this(null, null, null) { }
    public FlowerMangaDotNetScraper(LogService? logService) : this(logService, null, null) { }
    public FlowerMangaDotNetScraper(LogService? logService, CloudflareCredentialStore? cfStore) : this(logService, cfStore, null) { }

    internal FlowerMangaDotNetScraper(HttpMessageHandler handler) : this(null, null, handler) { }

    private FlowerMangaDotNetScraper(LogService? logService, CloudflareCredentialStore? cfStore, HttpMessageHandler? handler)
    {
        if (handler is null)
        {
            handler = new CloudflareHandler(new HttpClientHandler(), logService, cfStore);
            if (logService is not null)
                handler = new LoggingHttpHandler(logService, handler);
        }

        _http = new HttpClient(handler) { BaseAddress = new Uri($"{BaseUrl}/") };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
        _http.DefaultRequestHeaders.Referrer = new Uri($"{BaseUrl}/");
    }

    public bool CanHandle(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                                         uri.Host.Equals("flowermanga.org", StringComparison.OrdinalIgnoreCase);

    public async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var cover = document.QuerySelector(".hposter img, div.summary_image img")?.GetAttribute("src") ??
                    document.QuerySelector("meta[property='og:image']")?.GetAttribute("content") ?? string.Empty;
        var title = document.QuerySelector("h1")?.TextContent.Trim() ??
                    document.QuerySelector("meta[property='og:title']")?.GetAttribute("content")?.Trim() ?? string.Empty;
        var description = document.QuerySelector(".syn p, div.summary__content p")?.TextContent.Trim() ??
                          document.QuerySelector("meta[property='og:description']")?.GetAttribute("content")?.Trim() ?? string.Empty;

        return new Manga
        {
            Name = title,
            CoverUrl = ToAbsoluteUrl(url, cover),
            Description = description,
            Url = url,
            SiteName = Name
        };
    }

    public async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var json = document.QuerySelector("#mk-chapters-data")?.TextContent;
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var chaptersDocument = JsonDocument.Parse(json);
        if (!chaptersDocument.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<Chapter>();
        foreach (var item in items.EnumerateArray())
        {
            var chapterUrl = item.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var title = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var numberText = item.TryGetProperty("num", out var numberElement) ? numberElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(chapterUrl) || string.IsNullOrWhiteSpace(title))
                continue;

            _ = double.TryParse(numberText, NumberStyles.Any, CultureInfo.InvariantCulture, out var number);
            chapters.Add(new Chapter { Number = number, Title = title, Url = ToAbsoluteUrl(url, chapterUrl) });
        }

        return chapters;
    }

    public async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chapter.Url))
            return [];

        var document = await LoadDocumentAsync(chapter.Url, ct);
        var pages = new List<Page>();
        foreach (var image in document.QuerySelectorAll(".reading-content img.wp-manga-chapter-img, .page-break img"))
        {
            var source = image.GetAttribute("data-src") ?? image.GetAttribute("data-url") ?? image.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(source))
                continue;

            pages.Add(new Page
            {
                Number = pages.Count + 1,
                ImageUrl = ToAbsoluteUrl(chapter.Url, source),
                RefererUrl = chapter.Url
            });
        }

        return pages;
    }

    private async Task<IDocument> LoadDocumentAsync(string url, CancellationToken ct)
    {
        var html = await _http.GetStringAsync(url, ct);
        return await _browser.OpenAsync(request => request.Content(html).Address(url), ct);
    }

    private static string ToAbsoluteUrl(string baseUrl, string url)
    {
        var value = url.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        return new Uri(new Uri(baseUrl), value).ToString();
    }
}
