using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MuitoHentai;

public sealed class MuitoHentaiScraper : HtmlScraperBase
{
    public override string Name => "Muito Hentai";

    public MuitoHentaiScraper() : this(null, null) { }

    public MuitoHentaiScraper(LogService? logService) : this(logService, null) { }

    public MuitoHentaiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://www.muitohentai.com", logService, cfStore)
    { }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return new Manga
        {
            Name = document.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(document.QuerySelector("#capaAnime img"), url) ?? string.Empty,
            Description = document.QuerySelector("div.backgroundpost:contains(Sinopse)")?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return document.QuerySelectorAll("div.backgroundpost:contains(Capítulos de) h3 > a")
            .Select(element =>
            {
                var title = element.TextContent?.Trim() ?? string.Empty;
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
        var script = document.QuerySelector("script")?.TextContent ?? string.Empty;
        var arrayText = script.Contains("var arr =", StringComparison.Ordinal)
            ? script[(script.IndexOf("var arr =", StringComparison.Ordinal) + "var arr =".Length)..]
                .Split(';', 2, StringSplitOptions.TrimEntries)[0]
            : string.Empty;

        if (string.IsNullOrWhiteSpace(arrayText))
            return [];

        using var json = JsonDocument.Parse(arrayText);
        return json.RootElement.EnumerateArray()
            .Select((item, index) => new Page
            {
                Number = index + 1,
                ImageUrl = item.GetString() ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
