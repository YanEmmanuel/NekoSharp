using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.ExHentaiNetBR;

public sealed class ExHentaiNetBRScraper : HtmlScraperBase
{
    public override string Name => "ExHentai.net.br";

    public ExHentaiNetBRScraper() : this(null, null) { }

    public ExHentaiNetBRScraper(LogService? logService) : this(logService, null) { }

    public ExHentaiNetBRScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://exhentai.net.br", logService, cfStore)
    { }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return new Manga
        {
            Name = document.QuerySelector(".stats_box h3")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(document.QuerySelector(".anime_cover img"), url) ?? string.Empty,
            Description = document.QuerySelector(".sinopse_manga .info_p:last-child")?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return document.QuerySelectorAll(".chapter_content a")
            .Select(element =>
            {
                var title = element.QuerySelector(".name_chapter")?.TextContent?.Trim() ?? string.Empty;
                var href = ToAbsoluteUrl(url, element.GetAttribute("href"));
                return string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)
                    ? null
                    : new Chapter
                    {
                        Title = title,
                        Number = ChapterHelper.ExtractChapterNumber(title),
                        Url = href
                    };
            })
            .Where(chapter => chapter is not null)
            .Cast<Chapter>()
            .Reverse()
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var document = await LoadDocumentAsync(chapter.Url, ct);
        return document.QuerySelectorAll("div.manga_image > img")
            .Select((element, index) => new Page
            {
                Number = index + 1,
                ImageUrl = ExtractImageSource(element, chapter.Url) ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
