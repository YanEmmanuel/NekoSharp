using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.MediocreScan;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Azuretoons;

public sealed class AzuretoonsScraper : HtmlScraperBase
{
    public override string Name => "Azuretoons";

    private const string ApiUrl = "https://azuretoons.com/api";

    public AzuretoonsScraper() : this(null, null) { }

    public AzuretoonsScraper(LogService? logService) : this(logService, null) { }

    public AzuretoonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://azuretoons.com", logService, cfStore)
    {
        Http.DefaultRequestHeaders.Remove("Accept");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        Http.DefaultRequestHeaders.Remove("Pragma");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Pragma", "no-cache");
        Http.DefaultRequestHeaders.Remove("Origin");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", BaseUrl);
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var slug = GetLastSegment(url);
        var payload = await GetJsonAsync($"{ApiUrl}/obras/slug/{slug}", ct);
        return new Manga
        {
            Name = GetString(payload, "title"),
            CoverUrl = GetString(payload, "coverUrl"),
            Description = StripHtml(GetString(payload, "description")),
            Url = $"{BaseUrl}/obra/{slug}",
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var slug = GetLastSegment(url);
        var payload = await GetJsonAsync($"{ApiUrl}/obras/slug/{slug}", ct);
        if (!payload.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
            return [];

        return chapters.EnumerateArray()
            .Select(chapter =>
            {
                var number = GetDouble(chapter, "chapterNumber");
                var title = GetString(chapter, "title");
                return new Chapter
                {
                    Title = string.IsNullOrWhiteSpace(title) ? $"Capítulo {number}" : title,
                    Number = number,
                    Url = $"{BaseUrl}/obra/{slug}/capitulo/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                };
            })
            .OrderByDescending(chapter => chapter.Number)
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var slugMatch = Regex.Match(chapter.Url, @"/obra/([^/]+)/capitulo/", RegexOptions.IgnoreCase);
        if (!slugMatch.Success)
            return [];

        var slug = slugMatch.Groups[1].Value;
        var chapterId = chapter.Url[(chapter.Url.LastIndexOf('/') + 1)..];
        var payload = await GetJsonAsync($"{ApiUrl}/chapters/read/{slug}/{chapterId}", ct);
        if (!payload.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
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

    private static string GetLastSegment(string url)
        => new Uri(url).Segments.Last().Trim('/');

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var node) ? node.GetString() ?? string.Empty : string.Empty;

    private static double GetDouble(JsonElement element, string property)
        => element.TryGetProperty(property, out var node) && double.TryParse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0d;
}
