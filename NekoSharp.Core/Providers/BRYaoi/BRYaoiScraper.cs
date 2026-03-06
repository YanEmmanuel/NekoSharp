using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.BRYaoi;

public sealed class BRYaoiScraper : HtmlScraperBase
{
    private static readonly Regex ImageArrayRegex = new(@"imageArray\s*=\s*(\{.*?})", RegexOptions.Singleline | RegexOptions.Compiled);

    public override string Name => "BR Yaoi";

    public BRYaoiScraper() : this(null, null) { }

    public BRYaoiScraper(LogService? logService) : this(logService, null) { }

    public BRYaoiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://bryaoi.com", logService, cfStore)
    { }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var rawTitle = document.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty;
        return new Manga
        {
            Name = rawTitle.Replace("Ler", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("Online", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim(),
            CoverUrl = ExtractImageSource(document.QuerySelector(".serie-capa img"), url) ?? string.Empty,
            Description = string.Join("\n", document.QuerySelectorAll(".serie-texto p").Select(p => p.TextContent?.Trim()).Where(t => !string.IsNullOrWhiteSpace(t))),
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        return document.QuerySelectorAll(".capitulos a")
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
        var script = string.Join("\n", document.QuerySelectorAll("script").Select(node => node.TextContent));
        var match = ImageArrayRegex.Match(script);
        if (!match.Success)
            return [];

        using var json = JsonDocument.Parse(match.Groups[1].Value);
        if (!json.RootElement.TryGetProperty("images", out var images))
            return [];

        return images.EnumerateArray()
            .Select((item, index) => new Page
            {
                Number = index + 1,
                ImageUrl = item.GetString() ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
