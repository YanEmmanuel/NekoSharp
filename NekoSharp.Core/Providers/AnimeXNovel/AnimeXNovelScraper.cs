using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.MediocreScan;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.AnimeXNovel;

public sealed class AnimeXNovelScraper : HtmlScraperBase
{
    public override string Name => "AnimeXNovel";

    public AnimeXNovelScraper() : this(null, null) { }

    public AnimeXNovelScraper(LogService? logService) : this(logService, null) { }

    public AnimeXNovelScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://www.animexnovel.com", logService, cfStore)
    { }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return new Manga
        {
            Name = document.QuerySelector("h2.spnc-entry-title")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(document.QuerySelector("img"), url) ?? string.Empty,
            Description = document.QuerySelector("meta[itemprop='description']")?.GetAttribute("content") ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var category = document.QuerySelector("#container-capitulos")?.GetAttribute("data-categoria");
        if (string.IsNullOrWhiteSpace(category))
            return [];

        var builder = new UriBuilder($"{BaseUrl}/wp-json/wp/v2/posts");
        var query = $"categories={Uri.EscapeDataString(category)}&orderby=date&order=desc&per_page=100&page=1";
        builder.Query = query;

        var chapters = new List<Chapter>();
        var page = 1;
        while (true)
        {
            builder.Query = $"categories={Uri.EscapeDataString(category)}&orderby=date&order=desc&per_page=100&page={page}";
            using var response = await Http.GetAsync(builder.Uri, ct);
            if (!response.IsSuccessStatusCode)
                break;

            var payload = ParseJson(await response.Content.ReadAsStringAsync(ct));
            if (payload.ValueKind != JsonValueKind.Array || payload.GetArrayLength() == 0)
                break;

            chapters.AddRange(payload.EnumerateArray()
                .Select(item =>
                {
                    var title = item.GetProperty("title").GetProperty("rendered").GetString() ?? string.Empty;
                    var slug = GetString(item, "slug");
                    var normalized = title.Contains(';') ? title[(title.IndexOf(';') + 1)..].Trim() : title;
                    return new Chapter
                    {
                        Title = normalized,
                        Number = ChapterHelper.ExtractChapterNumber(normalized),
                        Url = $"{BaseUrl}/manga/{slug}"
                    };
                })
                .Where(chapter => chapter.Url.Contains("capitulo", StringComparison.OrdinalIgnoreCase)));

            page++;
        }

        return chapters;
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        var container = document.QuerySelector(".spice-block-img-gallery, .wp-block-gallery, .spnc-entry-content") ?? GetDocumentRoot(document);
        return container.QuerySelectorAll("img")
            .Select((element, index) => new Page
            {
                Number = index + 1,
                ImageUrl = ExtractImageSource(element, chapter.Url) ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var node) ? node.GetString() ?? string.Empty : string.Empty;
}
