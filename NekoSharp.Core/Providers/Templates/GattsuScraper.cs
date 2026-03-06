using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class GattsuScraper : HtmlScraperBase
{
    protected virtual string MangaRootSelector => "div.meio div.post-box";
    protected virtual string MangaTitleSelector => "h1.post-titulo";
    protected virtual string MangaCoverSelector => "div.post-capa > img.wp-post-image";
    protected virtual string MangaDescriptionSelector => "div.post-texto p";
    protected virtual string ChapterMarkerSelector => "div.meio div.post-box ul.post-fotos li a > img, div.meio div.post-box.listaImagens div.galeriaHtml img";
    protected virtual string PageSelector => "div.meio div.post-box ul.post-fotos li a > img, div.meio div.post-box.listaImagens div.galeriaHtml img";

    protected GattsuScraper(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
        : base(baseUrl, logService, cfStore)
    {
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var root = document.QuerySelector(MangaRootSelector) ?? GetDocumentRoot(document);
        var description = string.Join(
                "\n\n",
                root.QuerySelectorAll(MangaDescriptionSelector)
                    .Select(node => node.TextContent?.Trim())
                    .Where(text => !string.IsNullOrWhiteSpace(text)))
            .Replace("Sinopse :", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return new Manga
        {
            Name = root.QuerySelector(MangaTitleSelector)?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(root.QuerySelector(MangaCoverSelector), url)?.Replace("-150x150.", ".", StringComparison.OrdinalIgnoreCase) ?? string.Empty,
            Description = description,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        if (!document.QuerySelectorAll(ChapterMarkerSelector).Any())
            return [];

        return
        [
            new Chapter
            {
                Title = "Capítulo único",
                Number = 1,
                Url = url
            }
        ];
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        var pages = new List<Page>();
        var index = 1;

        foreach (var element in document.QuerySelectorAll(PageSelector))
        {
            var imageUrl = ExtractImageSource(element, chapter.Url)?.Replace("-150x150.", ".", StringComparison.OrdinalIgnoreCase);
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
}
