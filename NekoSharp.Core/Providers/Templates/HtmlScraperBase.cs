using System.Net;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class HtmlScraperBase : IScraper
{
    public abstract string Name { get; }
    public string BaseUrl { get; }

    protected readonly HttpClient Http;
    protected readonly IBrowsingContext Browser;
    protected readonly LogService? Log;

    protected virtual IReadOnlyCollection<string> SupportedHosts => [_baseUri.Host];

    private readonly Uri _baseUri;

    protected HtmlScraperBase(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("BaseUrl invalida.", nameof(baseUrl));

        BaseUrl = baseUrl.TrimEnd('/');
        _baseUri = new Uri(EnsureTrailingSlash(BaseUrl));
        Log = logService;
        Browser = BrowsingContext.New(Configuration.Default);

        var inner = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli
        };

        HttpMessageHandler handler = new CloudflareHandler(
            inner: inner,
            logService: logService,
            store: cfStore);

        if (logService is not null)
            handler = new LoggingHttpHandler(logService, handler);

        Http = new HttpClient(handler)
        {
            BaseAddress = _baseUri,
            Timeout = TimeSpan.FromSeconds(45)
        };

        Http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", _baseUri.ToString());
        Http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    public virtual bool CanHandle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return SupportedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
    }

    public abstract Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default);
    public abstract Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default);
    public abstract Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default);

    protected async Task<IDocument> LoadDocumentAsync(string url, CancellationToken ct = default)
    {
        var html = await GetStringAsync(url, ct);
        return await OpenDocumentAsync(html, url, ct);
    }

    protected async Task<IDocument> LoadDocumentAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        return await OpenDocumentAsync(html, response.RequestMessage?.RequestUri?.ToString() ?? request.RequestUri?.ToString() ?? BaseUrl, ct);
    }

    protected async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected async Task<string> SendForStringAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct = default)
    {
        var json = await GetStringAsync(url, ct);
        return ParseJson(json);
    }

    protected async Task<JsonElement> SendForJsonAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        var json = await SendForStringAsync(request, ct);
        return ParseJson(json);
    }

    protected async Task<IDocument> OpenDocumentAsync(string html, string url, CancellationToken ct = default)
    {
        return await Browser.OpenAsync(req => req.Content(html).Address(url), ct);
    }

    protected static IElement GetDocumentRoot(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Body ?? document.DocumentElement ?? throw new InvalidOperationException("Documento HTML sem elemento raiz.");
    }

    protected static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    protected static string EnsureTrailingSlash(string url)
        => url.EndsWith('/') ? url : $"{url}/";

    protected static string? ToAbsoluteUrl(string baseUrl, string? href)
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

    protected static string? ExtractImageSource(IElement? imageNode, string refererUrl)
    {
        if (imageNode is null)
            return null;

        var rawSrc = imageNode.GetAttribute("data-src-url") ??
                     imageNode.GetAttribute("data-src") ??
                     imageNode.GetAttribute("data-lazy-src") ??
                     imageNode.GetAttribute("data-lazy") ??
                     imageNode.GetAttribute("data-url") ??
                     imageNode.GetAttribute("srcset") ??
                     imageNode.GetAttribute("src") ??
                     imageNode.GetAttribute("href");

        return NormalizeImageSource(rawSrc) is { } normalized
            ? ToAbsoluteUrl(refererUrl, normalized)
            : null;
    }

    protected static List<Page> CreatePagesFromNodes(IEnumerable<IElement> elements, string refererUrl)
    {
        var pages = new List<Page>();
        var index = 1;

        foreach (var element in elements)
        {
            var imageUrl = element.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase) ||
                           element.TagName.Equals("IMAGE", StringComparison.OrdinalIgnoreCase) ||
                           element.TagName.Equals("A", StringComparison.OrdinalIgnoreCase)
                ? ExtractImageSource(element, refererUrl)
                : ExtractImageSource(element.QuerySelector("img, image, a"), refererUrl);

            if (string.IsNullOrWhiteSpace(imageUrl))
                continue;

            pages.Add(new Page
            {
                Number = index++,
                ImageUrl = imageUrl
            });
        }

        return pages;
    }

    protected static string? NormalizeImageSource(string? rawSrc)
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

    protected static string? GetFirstText(IParentNode parent, params string[] selectors)
    {
        foreach (var selector in selectors)
        {
            var value = parent.QuerySelector(selector)?.TextContent?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    protected static string? GetFirstAttribute(IParentNode parent, string attribute, params string[] selectors)
    {
        foreach (var selector in selectors)
        {
            var value = parent.QuerySelector(selector)?.GetAttribute(attribute)?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    protected static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", " ")
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    protected static Chapter CreateChapter(string title, string url)
    {
        return new Chapter
        {
            Title = title.Trim(),
            Url = url,
            Number = ChapterHelper.ExtractChapterNumber(title)
        };
    }
}
