using System.Text.Json;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class StalkerCmsScraper : HtmlScraperBase
{
    protected virtual string DetailsTitleSelector => "h1";
    protected virtual string DetailsThumbnailSelector => ".sidebar-cover-image img";
    protected virtual string DetailsDescriptionSelector => ".manga-description";
    protected virtual string ChapterListSelector => ".chapter-item-list a.chapter-link";
    protected virtual string ChapterNameSelector => ".chapter-number";
    protected virtual string PageListSelector => ".chapter-image-canvas";
    protected virtual string PageImageAttr => "data-src-url";

    protected StalkerCmsScraper(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
        : base(baseUrl, logService, cfStore)
    {
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return new Manga
        {
            Name = document.QuerySelector(DetailsTitleSelector)?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(document.QuerySelector(DetailsThumbnailSelector), url) ?? string.Empty,
            Description = document.QuerySelector(DetailsDescriptionSelector)?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var chapters = new List<Chapter>();
        var page = 1;

        while (true)
        {
            var separator = url.Contains('?') ? "&" : "?";
            var document = await LoadDocumentAsync($"{url}{separator}page={page}", ct);
            var current = document.QuerySelectorAll(ChapterListSelector)
                .Select(element => MapChapter(element, url))
                .Where(chapter => chapter is not null)
                .Cast<Chapter>()
                .ToList();

            chapters.AddRange(current);

            if (current.Count == 0 || document.QuerySelector(".page-link[aria-label=Próxima]:not(disabled)") is null)
                break;

            page++;
        }

        return chapters;
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        return document.QuerySelectorAll(PageListSelector)
            .Select((element, index) => new Page
            {
                Number = index + 1,
                ImageUrl = ToAbsoluteUrl(chapter.Url, element.GetAttribute(PageImageAttr)) ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }

    protected virtual Chapter? MapChapter(IElement element, string refererUrl)
    {
        var title = element.QuerySelector(ChapterNameSelector)?.TextContent?.Trim()
                    ?? element.TextContent?.Trim()
                    ?? string.Empty;
        var chapterUrl = ToAbsoluteUrl(refererUrl, element.GetAttribute("href"));

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(chapterUrl))
            return null;

        return new Chapter
        {
            Title = title,
            Url = chapterUrl,
            Number = ChapterHelper.ExtractChapterNumber(title)
        };
    }
}
