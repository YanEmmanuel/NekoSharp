using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.MediocreScan;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Astratoons;

public sealed class AstratoonsScraper : HtmlScraperBase
{
    private static readonly Regex MangaIdRegex = new(@"\d+", RegexOptions.Compiled);

    public override string Name => "Astratoons";

    public AstratoonsScraper() : this(null, null) { }

    public AstratoonsScraper(LogService? logService) : this(logService, null) { }

    public AstratoonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://new.astratoons.com", logService, cfStore)
    { }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return new Manga
        {
            Name = document.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(document.QuerySelector("img[class*=object-cover]"), url) ?? string.Empty,
            Description = document.QuerySelector("div:has(>h1) + div")?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var xData = document.QuerySelector("main[x-data]")?.GetAttribute("x-data") ?? string.Empty;
        var mangaId = MangaIdRegex.Match(xData).Value;
        if (string.IsNullOrWhiteSpace(mangaId))
            return [];

        var chapters = new List<Chapter>();
        var page = 1;
        while (true)
        {
            var payload = await GetJsonAsync($"{BaseUrl}/api/comics/{mangaId}/chapters?search=&order=desc&page={page}", ct);
            var html = payload.TryGetProperty("html", out var htmlNode) ? htmlNode.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(html))
                break;

            var fragment = await OpenDocumentAsync($"<body>{html}</body>", url, ct);
            var current = fragment.QuerySelectorAll("a")
                .Select(element =>
                {
                    var title = element.QuerySelector(".text-lg")?.TextContent?.Trim() ?? string.Empty;
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
                .ToList();

            if (current.Count == 0)
                break;

            chapters.AddRange(current);

            if (!payload.TryGetProperty("hasMore", out var hasMore) || !hasMore.GetBoolean())
                break;

            page++;
        }

        return chapters;
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        return document.QuerySelectorAll("#reader-container img[src], #reader-container canvas[data-src]")
            .Select((element, index) =>
            {
                var imageUrl = element.GetAttribute("src");
                if (string.IsNullOrWhiteSpace(imageUrl))
                    imageUrl = element.GetAttribute("data-src");

                return new Page
                {
                    Number = index + 1,
                    ImageUrl = ToAbsoluteUrl(chapter.Url, imageUrl) ?? string.Empty
                };
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
