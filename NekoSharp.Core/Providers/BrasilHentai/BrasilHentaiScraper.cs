using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.BrasilHentai;

public sealed class BrasilHentaiScraper : HtmlScraperBase
{
    public override string Name => "Brasil Hentai";

    public BrasilHentaiScraper() : this(null, null) { }

    public BrasilHentaiScraper(LogService? logService) : this(logService, null) { }

    public BrasilHentaiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://brasilhentai.com", logService, cfStore)
    { }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return new Manga
        {
            Name = document.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(document.QuerySelector(".entry-content p a img"), url) ?? string.Empty,
            Description = string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    public override Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        return Task.FromResult(new List<Chapter>
        {
            new()
            {
                Title = "Capítulo único",
                Number = 1,
                Url = url
            }
        });
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var document = await LoadDocumentAsync(chapter.Url, ct);
        return document.QuerySelectorAll(".entry-content p > img")
            .Select((element, index) => new Page
            {
                Number = index + 1,
                ImageUrl = ExtractImageSource(element, chapter.Url) ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
