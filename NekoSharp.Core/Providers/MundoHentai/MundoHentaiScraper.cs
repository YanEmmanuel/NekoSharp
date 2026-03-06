using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MundoHentai;

public sealed class MundoHentaiScraper : HtmlScraperBase
{
    public override string Name => "Mundo Hentai";

    public MundoHentaiScraper() : this(null, null) { }

    public MundoHentaiScraper(LogService? logService) : this(logService, null) { }

    public MundoHentaiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://mundohentaioficial.com", logService, cfStore)
    { }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return new Manga
        {
            Name = document.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(document.QuerySelector("div.post-capa img"), url) ?? string.Empty,
            Description = document.QuerySelector("ul.post-itens li:contains(Cor:)")?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var tabs = document.QuerySelectorAll("div.listaImagens div.galeriaTab");
        if (tabs.Length == 0)
        {
            return
            [
                new Chapter
                {
                    Title = "Capítulo",
                    Number = 1,
                    Url = url
                }
            ];
        }

        return tabs.Select(tab =>
            {
                var chapterId = tab.GetAttribute("data-id") ?? string.Empty;
                var title = tab.QuerySelector("div.galeriaTabTitulo")?.TextContent?.Trim();
                var fullTitle = $"Capítulo {chapterId}" + (string.IsNullOrWhiteSpace(title) ? string.Empty : $" - {title}");
                return new Chapter
                {
                    Title = fullTitle,
                    Number = double.TryParse(chapterId, out var parsed) ? parsed : ChapterHelper.ExtractChapterNumber(fullTitle),
                    Url = $"{url}#{chapterId}"
                };
            })
            .Reverse()
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var document = await LoadDocumentAsync(chapter.Url.Split('#')[0], ct);
        var chapterId = chapter.Url.Contains('#') ? chapter.Url[(chapter.Url.LastIndexOf('#') + 1)..] : string.Empty;
        var selector = string.IsNullOrWhiteSpace(chapterId)
            ? "div.listaImagens ul.post-fotos img"
            : $"div.listaImagens #galeria-{chapterId} img";

        return document.QuerySelectorAll(selector)
            .Select((element, index) => new Page
            {
                Number = index + 1,
                ImageUrl = ExtractImageSource(element, chapter.Url) ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
