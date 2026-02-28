using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class WordPressMadaraScraper : IScraper
{
    public abstract string Name { get; }
    public string BaseUrl { get; }

    protected virtual string PathPrefix => string.Empty;

    protected virtual string MangaCoverSelector => "div.summary_image img";
    protected virtual string MangaTitleSelector => "h1";
    protected virtual string MangaOgTitleSelector => "head meta[property=\"og:title\"]";
    protected virtual string MangaDescriptionSelector => "div.summary__content p";

    protected virtual string ChaptersSelector => "li.wp-manga-chapter > a";
    protected virtual string ChaptersPlaceholderSelector => "[id^=\"manga-chapters-holder\"][data-id]";
    protected virtual bool EnableChapterAjax => true;
    protected virtual bool ReverseChapterOrder => false;

    protected virtual bool UseStyleListQuery => true;
    protected virtual string PagesSelector => "div.page-break.no-gaps, div.page-break";
    protected virtual string PageImageSelector => "img, image";
    protected virtual string FallbackPageImageSelector => "img.wp-manga-chapter-img";

    protected readonly HttpClient Http;
    protected readonly IBrowsingContext Browser;

    protected WordPressMadaraScraper(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("BaseUrl invalida.", nameof(baseUrl));

        BaseUrl = baseUrl.TrimEnd('/');
        Browser = BrowsingContext.New(Configuration.Default);

        HttpMessageHandler inner = new CloudflareHandler(
            inner: new HttpClientHandler(),
            logService: logService,
            store: cfStore);

        HttpMessageHandler handler = logService != null
            ? new LoggingHttpHandler(logService, inner)
            : inner;

        Http = new HttpClient(handler);
        Http.BaseAddress = new Uri(EnsureTrailingSlash(BaseUrl));
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
        Http.DefaultRequestHeaders.Add("Referer", EnsureTrailingSlash(BaseUrl));
        Http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        Http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    public bool CanHandle(string url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    public virtual async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException($"URL invalida do {Name}.", nameof(url));

        var doc = await LoadDocumentAsync(url, ct);

        var coverImg = doc.QuerySelector<IHtmlImageElement>(MangaCoverSelector);
        var coverUrl = coverImg?.Source ?? string.Empty;
        var titleFromAlt = coverImg?.AlternativeText?.Trim() ?? string.Empty;

        var titleNode = doc.QuerySelector(MangaTitleSelector);
        var titleFromNode = titleNode?.TextContent?.Trim() ?? string.Empty;
        var titleFromMeta = doc.QuerySelector(MangaOgTitleSelector)?.GetAttribute("content")?.Trim() ?? string.Empty;
        var title = FirstNonEmpty(titleFromNode, titleFromAlt, titleFromMeta);

        var descriptionNode = doc.QuerySelector(MangaDescriptionSelector);
        var descriptionText = descriptionNode?.TextContent?.Trim() ?? string.Empty;

        return new Manga
        {
            Name = title,
            CoverUrl = coverUrl,
            Url = url,
            Description = descriptionText,
            SiteName = Name
        };
    }

    public virtual async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException($"URL invalida do {Name}.", nameof(url));

        var doc = await LoadDocumentAsync(url, ct);

        IEnumerable<IElement> chapterLinks = doc.QuerySelectorAll(ChaptersSelector);

        if (EnableChapterAjax && doc.QuerySelector(ChaptersPlaceholderSelector) is { } placeholder)
        {
            var ajaxLinks = await TryLoadChaptersFromAjaxAsync(url, placeholder, ct);
            if (ajaxLinks.Count > 0)
                chapterLinks = ajaxLinks;
        }

        var chapters = ParseChapters(chapterLinks, url);

        if (ReverseChapterOrder)
            chapters.Reverse();

        return chapters;
    }

    public virtual async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        if (string.IsNullOrWhiteSpace(chapter.Url))
            return [];

        var requestUrl = chapter.Url;
        if (UseStyleListQuery)
            requestUrl = AddOrUpdateQueryParameter(requestUrl, "style", "list");

        var doc = await LoadDocumentAsync(requestUrl, ct);
        var pageNodes = doc.QuerySelectorAll(PagesSelector);

        if (pageNodes.Length == 0 && UseStyleListQuery)
        {
            requestUrl = RemoveQueryParameter(requestUrl, "style");
            doc = await LoadDocumentAsync(requestUrl, ct);
            pageNodes = doc.QuerySelectorAll(PagesSelector);
        }

        var pages = new List<Page>();
        var index = 1;

        if (pageNodes.Length > 0)
        {
            foreach (var node in pageNodes)
            {
                var src = ExtractPageImageUrl(node, requestUrl);
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

        var fallbackImages = doc.QuerySelectorAll(FallbackPageImageSelector);
        foreach (var img in fallbackImages)
        {
            var src = ExtractImageSource(img, requestUrl);
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

    protected virtual string NormalizeChapterTitle(string rawTitle)
    {
        return rawTitle.Contains(" - ", StringComparison.Ordinal)
            ? rawTitle.Split(" - ", 2, StringSplitOptions.None)[1].Trim()
            : rawTitle;
    }

    protected virtual string? ExtractPageImageUrl(IElement pageElement, string refererUrl)
    {
        var imageNode = pageElement.Matches(PageImageSelector)
            ? pageElement
            : pageElement.QuerySelector(PageImageSelector);

        return imageNode is null ? null : ExtractImageSource(imageNode, refererUrl);
    }

    protected virtual string? ExtractImageSource(IElement imageNode, string refererUrl)
    {
        var rawSrc = imageNode.GetAttribute("data-url") ??
                     imageNode.GetAttribute("data-src") ??
                     imageNode.GetAttribute("data-lazy") ??
                     imageNode.GetAttribute("srcset") ??
                     imageNode.GetAttribute("src");

        var src = NormalizeImageSource(rawSrc);
        if (string.IsNullOrWhiteSpace(src))
            return null;

        if (src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            return src.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var absolute = ToAbsoluteUrl(refererUrl, src);
        if (string.IsNullOrWhiteSpace(absolute))
            return null;

        return ResolveCanonicalSource(absolute);
    }

    private List<Chapter> ParseChapters(IEnumerable<IElement> chapterLinks, string refererUrl)
    {
        var chapters = new List<Chapter>();

        foreach (var link in chapterLinks)
        {
            var chapterUrl = ToAbsoluteUrl(refererUrl, link.GetAttribute("href"));
            var rawTitle = (link.TextContent ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(chapterUrl) || string.IsNullOrWhiteSpace(rawTitle))
                continue;

            chapters.Add(new Chapter
            {
                Number = ChapterHelper.ExtractChapterNumber(rawTitle),
                Title = NormalizeChapterTitle(rawTitle),
                Url = chapterUrl
            });
        }

        return chapters;
    }

    private async Task<List<IElement>> TryLoadChaptersFromAjaxAsync(string mangaUrl, IElement placeholder, CancellationToken ct)
    {
        try
        {
            var links = await LoadChaptersFromNewAjaxAsync(mangaUrl, ct);
            if (links.Count > 0)
                return links;
        }
        catch
        {
        }

        var dataId = placeholder.GetAttribute("data-id");
        if (string.IsNullOrWhiteSpace(dataId))
            return [];

        try
        {
            return await LoadChaptersFromOldAjaxAsync(dataId, ct);
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<IElement>> LoadChaptersFromNewAjaxAsync(string mangaUrl, CancellationToken ct)
    {
        var normalizedMangaUrl = EnsureTrailingSlash(mangaUrl);
        var endpoint = new Uri(new Uri(normalizedMangaUrl), "ajax/chapters/");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Referrer = new Uri(normalizedMangaUrl);

        var html = await SendForHtmlAsync(request, ct);
        var doc = await OpenDocumentAsync(html, endpoint.ToString(), ct);
        return doc.QuerySelectorAll(ChaptersSelector).ToList();
    }

    private async Task<List<IElement>> LoadChaptersFromOldAjaxAsync(string dataId, CancellationToken ct)
    {
        var path = CombinePath(PathPrefix, "wp-admin/admin-ajax.php");
        var endpoint = new Uri(new Uri(EnsureTrailingSlash(BaseUrl)), path);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("action", "manga_get_chapters"),
                new KeyValuePair<string, string>("manga", dataId)
            ])
        };

        request.Headers.Referrer = new Uri(EnsureTrailingSlash(BaseUrl));
        request.Headers.TryAddWithoutValidation("x-referer", BaseUrl);

        var html = await SendForHtmlAsync(request, ct);
        var doc = await OpenDocumentAsync(html, endpoint.ToString(), ct);
        return doc.QuerySelectorAll(ChaptersSelector).ToList();
    }

    private async Task<IDocument> LoadDocumentAsync(string url, CancellationToken ct)
    {
        var html = await Http.GetStringAsync(url, ct);
        return await OpenDocumentAsync(html, url, ct);
    }

    private async Task<IDocument> OpenDocumentAsync(string html, string url, CancellationToken ct)
    {
        return await Browser.OpenAsync(req => req.Content(html).Address(url), ct);
    }

    private async Task<string> SendForHtmlAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static string EnsureTrailingSlash(string url)
    {
        return url.EndsWith('/') ? url : $"{url}/";
    }

    private static string CombinePath(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second.TrimStart('/');

        return $"{first.Trim('/')}/{second.TrimStart('/')}";
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string? NormalizeImageSource(string? rawSrc)
    {
        if (string.IsNullOrWhiteSpace(rawSrc))
            return null;

        var value = rawSrc.Trim();

        if (value.Contains(','))
            value = value.Split(',', 2, StringSplitOptions.None)[0].Trim();

        if (value.Contains(' '))
            value = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        return value;
    }

    private static string? ToAbsoluteUrl(string baseUrl, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return null;

        return Uri.TryCreate(baseUri, href, out var combined)
            ? combined.ToString()
            : null;
    }

    private static string ResolveCanonicalSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var srcParam = GetQueryValue(uri.Query, "src");
        return !string.IsNullOrWhiteSpace(srcParam) && Uri.TryCreate(srcParam, UriKind.Absolute, out _)
            ? srcParam
            : url;
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.None);
            var currentKey = Uri.UnescapeDataString(pair[0]);

            if (!currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            return pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
        }

        return null;
    }

    private static string AddOrUpdateQueryParameter(string url, string key, string value)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var query = ParseQuery(uri.Query);
        query[key] = value;
        return BuildUrl(uri, query);
    }

    private static string RemoveQueryParameter(string url, string key)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var query = ParseQuery(uri.Query);
        query.Remove(key);
        return BuildUrl(uri, query);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(query))
            return result;

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.None);
            var key = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static string BuildUrl(Uri uri, Dictionary<string, string> query)
    {
        var builder = new UriBuilder(uri)
        {
            Query = string.Join("&", query.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"))
        };

        return builder.Uri.ToString();
    }
}
