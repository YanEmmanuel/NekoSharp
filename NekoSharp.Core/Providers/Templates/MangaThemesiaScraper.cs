using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class MangaThemesiaScraper : HtmlScraperBase
{
    protected virtual string SeriesDetailsSelector => "div.bigcontent, div.animefull, div.main-info, div.postbody";
    protected virtual string SeriesTitleSelector => "h1.entry-title, .ts-breadcrumb li:last-child span";
    protected virtual string SeriesDescriptionSelector => ".desc, .entry-content[itemprop=description]";
    protected virtual string SeriesAltNameSelector =>
        ".alternative, .wd-full:contains(alt) span, .alter, .seriestualt, .infotable tr:contains(Alternative) td:last-child";
    protected virtual string SeriesThumbnailSelector => ".infomanga > div[itemprop=image] img, .thumb img";

    protected virtual string ChapterListSelector => "div.bxcl li, div.cl li, #chapterlist li, ul li:has(div.chbox):has(div.eph-num)";
    protected virtual string ChapterUrlSelector => "a";
    protected virtual string ChapterNameSelector => ".lch a, .chapternum, a";

    protected virtual string PageSelector => "div#readerarea img";

    private static readonly Regex JsonImageListRegex = new("\"images\"\\s*:\\s*(\\[.*?])", RegexOptions.Singleline | RegexOptions.Compiled);

    protected MangaThemesiaScraper(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
        : base(baseUrl, logService, cfStore)
    {
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var details = document.QuerySelector(SeriesDetailsSelector) ?? GetDocumentRoot(document);

        var title = details.QuerySelector(SeriesTitleSelector)?.TextContent?.Trim()
                    ?? document.QuerySelector("h1")?.TextContent?.Trim()
                    ?? string.Empty;

        var description = string.Join(
                "\n",
                details.QuerySelectorAll(SeriesDescriptionSelector)
                    .Select(node => node.TextContent?.Trim())
                    .Where(text => !string.IsNullOrWhiteSpace(text)))
            .Trim();

        var altName = details.QuerySelector(SeriesAltNameSelector)?.TextContent?.Trim();
        if (!string.IsNullOrWhiteSpace(altName))
            description = string.IsNullOrWhiteSpace(description) ? altName : $"{description}\n\n{altName}";

        return new Manga
        {
            Name = title,
            CoverUrl = ExtractImageSource(details.QuerySelector(SeriesThumbnailSelector), url) ?? string.Empty,
            Description = description,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return document.QuerySelectorAll(ChapterListSelector)
            .Select(element => MapChapter(element, url))
            .Where(chapter => chapter is not null)
            .Cast<Chapter>()
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        return ParsePages(document, chapter.Url);
    }

    protected virtual Chapter? MapChapter(IElement element, string refererUrl)
    {
        var anchor = element.QuerySelector(ChapterUrlSelector);
        var url = ToAbsoluteUrl(refererUrl, anchor?.GetAttribute("href"));
        var title = element.QuerySelector(ChapterNameSelector)?.TextContent?.Trim()
                    ?? anchor?.TextContent?.Trim()
                    ?? string.Empty;

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
            return null;

        return new Chapter
        {
            Title = title,
            Url = url,
            Number = ChapterHelper.ExtractChapterNumber(title)
        };
    }

    protected virtual List<Page> ParsePages(IDocument document, string chapterUrl)
    {
        var htmlPages = CreatePagesFromNodes(
            document.QuerySelectorAll(PageSelector).Where(node => !string.IsNullOrWhiteSpace(ExtractImageSource(node, chapterUrl))),
            chapterUrl);

        if (htmlPages.Count > 0)
            return htmlPages;

        var match = JsonImageListRegex.Match(document.DocumentElement?.OuterHtml ?? string.Empty);
        if (!match.Success)
            return [];

        using var imageDoc = JsonDocument.Parse(match.Groups[1].Value);
        var pages = new List<Page>();
        var index = 1;

        foreach (var image in imageDoc.RootElement.EnumerateArray())
        {
            var imageUrl = image.GetString();
            if (string.IsNullOrWhiteSpace(imageUrl))
                continue;

            pages.Add(new Page
            {
                Number = index++,
                ImageUrl = ToAbsoluteUrl(chapterUrl, imageUrl) ?? imageUrl
            });
        }

        return pages;
    }
}
