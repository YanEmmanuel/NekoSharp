using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class PeachScanScraper : HtmlScraperBase
{
    protected virtual string MangaTitleSelector => ".desc__titulo__comic";
    protected virtual string MangaCoverSelector => ".sumario__img";
    protected virtual string MangaCategorySelector => ".categoria__comic";
    protected virtual string MangaDescriptionSelector => ".sumario__sinopse__texto";
    protected virtual string ChapterSelector => ".link__capitulos";
    protected virtual string PageScriptSelector => "script:contains(const urls)";
    protected virtual string PageContainerSelector => "#imageContainer img";

    private static readonly Regex UrlsRegex = new(@"const\s+urls\s*=\s*\[(.*?)]\s*;", RegexOptions.Singleline | RegexOptions.Compiled);

    protected PeachScanScraper(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
        : base(baseUrl, logService, cfStore)
    {
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var category = document.QuerySelector(MangaCategorySelector)?.TextContent?.Trim();
        var synopsis = document.QuerySelector(MangaDescriptionSelector)?.TextContent?.Trim();

        return new Manga
        {
            Name = document.QuerySelector(MangaTitleSelector)?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(document.QuerySelector(MangaCoverSelector), url) ?? string.Empty,
            Description = string.IsNullOrWhiteSpace(category) ? synopsis ?? string.Empty : $"Tipo: {category}\n\n{synopsis}".Trim(),
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return document.QuerySelectorAll(ChapterSelector)
            .Select(element =>
            {
                var title = element.QuerySelector(".numero__capitulo")?.TextContent?.Trim()
                            ?? element.TextContent?.Trim()
                            ?? string.Empty;
                var chapterUrl = ToAbsoluteUrl(url, element.GetAttribute("href"));

                return string.IsNullOrWhiteSpace(chapterUrl) || string.IsNullOrWhiteSpace(title)
                    ? null
                    : new Chapter
                    {
                        Title = title,
                        Url = chapterUrl,
                        Number = ChapterHelper.ExtractChapterNumber(title)
                    };
            })
            .Where(chapter => chapter is not null)
            .Cast<Chapter>()
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        var script = document.QuerySelector(PageScriptSelector)?.TextContent;
        if (!string.IsNullOrWhiteSpace(script))
        {
            var match = UrlsRegex.Match(script);
            if (match.Success)
            {
                return match.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim().Trim('\'', '"'))
                    .Where(src => !string.IsNullOrWhiteSpace(src))
                    .Select((src, index) => new Page
                    {
                        Number = index + 1,
                        ImageUrl = ToAbsoluteUrl(BaseUrl, src) ?? src
                    })
                    .ToList();
            }
        }

        return CreatePagesFromNodes(document.QuerySelectorAll(PageContainerSelector), chapter.Url);
    }
}
