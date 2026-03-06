using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.MediocreScan;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.ArgosScan;

public sealed class ArgosScanScraper : HtmlScraperBase
{
    private static readonly Regex SimpleNumberRegex = new(@"\d+(\.?\d+)?", RegexOptions.Compiled);

    public override string Name => "Argos Scan";

    public ArgosScanScraper() : this(null, null) { }

    public ArgosScanScraper(LogService? logService) : this(logService, null) { }

    public ArgosScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://argoscomics.online", logService, cfStore)
    { }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return new Manga
        {
            Name = document.QuerySelector(".content h2")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(document.QuerySelector(".trailer-box img"), url) ?? string.Empty,
            Description = document.QuerySelector(".content p")?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return document.QuerySelectorAll(".manga-chapter")
            .Select(element =>
            {
                var title = element.QuerySelector("h5")?.TextContent?.Trim() ?? string.Empty;
                var href = ToAbsoluteUrl(url, element.QuerySelector("a")?.GetAttribute("href"));
                var match = SimpleNumberRegex.Match(title);
                var number = match.Success && double.TryParse(match.Value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : ChapterHelper.ExtractChapterNumber(title);

                return string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)
                    ? null
                    : new Chapter
                    {
                        Title = title,
                        Number = number,
                        Url = href
                    };
            })
            .Where(chapter => chapter is not null)
            .Cast<Chapter>()
            .OrderByDescending(chapter => chapter.Number)
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        return document.QuerySelectorAll(".manga-page img")
            .Select((element, index) => new Page
            {
                Number = index + 1,
                ImageUrl = ExtractImageSource(element, chapter.Url) ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
