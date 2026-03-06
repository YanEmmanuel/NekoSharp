using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.MediocreScan;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.ToonBr;

public sealed class ToonBrScraper : HtmlScraperBase
{
    public override string Name => "ToonBr";

    private const string ApiUrl = "https://api.toonbr.com";
    private const string CdnUrl = "https://cdn.toonbr.com";

    public ToonBrScraper() : this(null, null) { }

    public ToonBrScraper(LogService? logService) : this(logService, null) { }

    public ToonBrScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://beta.toonbr.com", logService, cfStore)
    {
        Http.DefaultRequestHeaders.Remove("Accept");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var slug = GetLastSegment(url);
        var payload = await GetJsonAsync($"{ApiUrl}/api/manga/{slug}", ct);
        return new Manga
        {
            Name = GetString(payload, "title"),
            CoverUrl = $"{CdnUrl}{GetString(payload, "coverImage")}",
            Description = StripHtml(GetString(payload, "description")),
            Url = $"{BaseUrl}/manga/{slug}",
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var slug = GetLastSegment(url);
        var payload = await GetJsonAsync($"{ApiUrl}/api/manga/{slug}", ct);
        if (!payload.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
            return [];

        return chapters.EnumerateArray()
            .Select(chapter =>
            {
                var chapterId = GetString(chapter, "id");
                var number = GetNullableDouble(chapter, "chapterNumber") ?? 0d;
                var title = number > 0 ? $"Capítulo {number.ToString(System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')}" : GetString(chapter, "title");
                return string.IsNullOrWhiteSpace(chapterId)
                    ? null
                    : new Chapter
                    {
                        Title = title,
                        Number = number,
                        Url = $"{BaseUrl}/chapter/{chapterId}"
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

        var chapterId = GetLastSegment(chapter.Url);
        var payload = await GetJsonAsync($"{ApiUrl}/api/chapter/{chapterId}", ct);
        if (!payload.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return [];

        return pages.EnumerateArray()
            .Select((page, index) =>
            {
                var imageUrl = GetString(page, "imageUrl");
                return new Page
                {
                    Number = index + 1,
                    ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? string.Empty : $"{CdnUrl}{imageUrl}"
                };
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }

    private static string GetLastSegment(string url)
        => new Uri(url).Segments.Last().Trim('/');

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var node) ? node.GetString() ?? string.Empty : string.Empty;

    private static double? GetNullableDouble(JsonElement element, string property)
        => element.TryGetProperty(property, out var node) && double.TryParse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
