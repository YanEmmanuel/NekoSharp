using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class ZeistMangaScraper : HtmlScraperBase
{
    protected virtual string MangaCategory => "Series";
    protected virtual string ChapterCategory => "Chapter";
    protected virtual string MangaDetailsSelector => ".grid.gtc-235fr";
    protected virtual string MangaDescriptionSelector => "#synopsis";
    protected virtual string MangaThumbnailSelector => "img, .thumb, .thum";
    protected virtual string PageListSelector => "div.check-box div.separator";
    protected virtual bool SupportsLatestFromFeed => true;
    protected virtual bool UseNewChapterFeed => false;
    protected virtual bool UseOldChapterFeed => false;

    private static readonly Regex ChapterFeedRegex = new(@"clwd\.run\([""'](.*?)[""']\)", RegexOptions.Compiled);
    private static readonly Regex OldChapterFeedRegex = new(@"([^']+)\?", RegexOptions.Compiled);
    private static readonly Regex NewChapterFeedRegex = new(@"label\s*=\s*'([^']+)'", RegexOptions.Compiled);

    protected ZeistMangaScraper(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
        : base(baseUrl, logService, cfStore)
    {
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return ParseMangaInfo(document, url);
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var feedUrl = GetChapterFeedUrl(document);
        var json = await GetJsonAsync(feedUrl, ct);
        return OrderChapters(ParseChapterFeed(json, url)).ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        return ParsePages(document, chapter.Url);
    }

    protected virtual Manga ParseMangaInfo(IDocument document, string url)
    {
        var root = document.QuerySelector(MangaDetailsSelector) ?? GetDocumentRoot(document);
        var title = document.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty;
        var description = root.QuerySelector(MangaDescriptionSelector)?.TextContent?.Trim() ?? string.Empty;

        return new Manga
        {
            Name = title,
            CoverUrl = ResolveThumbnail(root, url),
            Description = description,
            Url = url,
            SiteName = Name
        };
    }

    protected virtual IEnumerable<Chapter> OrderChapters(IEnumerable<Chapter> chapters) => chapters;

    protected virtual string ResolveThumbnail(IElement root, string refererUrl)
    {
        var image = root.QuerySelector("img");
        if (image is not null)
            return ExtractImageSource(image, refererUrl) ?? string.Empty;

        var style = root.QuerySelector(".thumb, .thum")?.GetAttribute("style") ?? string.Empty;
        var quoted = Regex.Match(style, "url\\([\"']?(.*?)[\"']?\\)");
        return quoted.Success
            ? ToAbsoluteUrl(refererUrl, quoted.Groups[1].Value) ?? string.Empty
            : string.Empty;
    }

    protected virtual List<Page> ParsePages(IDocument document, string chapterUrl)
    {
        return CreatePagesFromNodes(document.QuerySelectorAll(PageListSelector), chapterUrl);
    }

    protected virtual string GetChapterFeedUrl(IDocument document)
    {
        if (UseNewChapterFeed)
            return BuildNewChapterFeedUrl(document);

        if (UseOldChapterFeed)
            return BuildOldChapterFeedUrl(document);

        var script = document.QuerySelector("#clwd > script");
        if (script is not null)
        {
            var match = ChapterFeedRegex.Match(script.InnerHtml);
            if (match.Success)
                return BuildFeedApiUrl(match.Groups[1].Value, ChapterCategory);
        }

        try
        {
            return BuildOldChapterFeedUrl(document);
        }
        catch
        {
            return BuildNewChapterFeedUrl(document);
        }
    }

    protected virtual List<Chapter> ParseChapterFeed(JsonElement root, string refererUrl)
    {
        var chapters = new List<Chapter>();

        if (!TryGetEntries(root, out var entries))
            return chapters;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!HasCategory(entry, ChapterCategory))
                continue;

            var href = GetAlternateHref(entry);
            var title = GetNestedString(entry, "title", "$t");
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title))
                continue;

            chapters.Add(new Chapter
            {
                Title = title.Trim(),
                Url = ToAbsoluteUrl(refererUrl, href) ?? href,
                Number = ChapterHelper.ExtractChapterNumber(title)
            });
        }

        return chapters;
    }

    protected string BuildFeedApiUrl(string feed, string category)
    {
        return $"{BaseUrl}/feeds/posts/default/-/{Uri.EscapeDataString(category)}/{feed.Trim('/')}" +
               "?alt=json&start-index=1&max-results=999";
    }

    protected string BuildApiUrl(string category)
    {
        return $"{BaseUrl}/feeds/posts/default/-/{Uri.EscapeDataString(category)}?alt=json";
    }

    private string BuildOldChapterFeedUrl(IDocument document)
    {
        var scriptSrc = document.QuerySelector("#myUL > script")?.GetAttribute("src")
                       ?? throw new InvalidOperationException("Feed antigo não encontrado.");
        var match = OldChapterFeedRegex.Match(scriptSrc);
        if (!match.Success)
            throw new InvalidOperationException("Não foi possível extrair o feed antigo.");

        return $"{BaseUrl}{match.Groups[1].Value}?alt=json&start-index=1&max-results=999";
    }

    private string BuildNewChapterFeedUrl(IDocument document)
    {
        var script = document.QuerySelector("#latest > script")?.InnerHtml
                     ?? document.QuerySelector("#clwd > script")?.InnerHtml
                     ?? throw new InvalidOperationException("Feed novo não encontrado.");
        var match = NewChapterFeedRegex.Match(script);
        if (!match.Success)
            throw new InvalidOperationException("Não foi possível extrair o feed novo.");

        return $"{BaseUrl}/feeds/posts/default/-/{Uri.EscapeDataString(ChapterCategory)}/{Uri.EscapeDataString(match.Groups[1].Value)}?alt=json&start-index=1&max-results=999";
    }

    private static bool TryGetEntries(JsonElement root, out JsonElement entries)
    {
        entries = default;
        return root.TryGetProperty("feed", out var feed) &&
               feed.TryGetProperty("entry", out entries) &&
               entries.ValueKind == JsonValueKind.Array;
    }

    private static bool HasCategory(JsonElement entry, string category)
    {
        if (!entry.TryGetProperty("category", out var categories) || categories.ValueKind != JsonValueKind.Array)
            return false;

        return categories.EnumerateArray().Any(item =>
            item.TryGetProperty("term", out var term) &&
            term.GetString()?.Equals(category, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string? GetAlternateHref(JsonElement entry)
    {
        if (!entry.TryGetProperty("link", out var links) || links.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var link in links.EnumerateArray())
        {
            var relValue = link.TryGetProperty("rel", out var rel) ? rel.GetString() : null;
            if (!string.Equals(relValue, "alternate", StringComparison.OrdinalIgnoreCase))
                continue;

            if (link.TryGetProperty("href", out var href))
                return href.GetString();
        }

        return null;
    }

    protected static string? GetNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (!current.TryGetProperty(part, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : current.ToString();
    }
}
