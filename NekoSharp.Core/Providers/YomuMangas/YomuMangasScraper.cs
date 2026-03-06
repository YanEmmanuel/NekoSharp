using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.MediocreScan;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.YomuMangas;

public sealed class YomuMangasScraper : HtmlScraperBase
{
    public override string Name => "Yomu Mangás";

    private const string ApiUrl = "https://api.yomumangas.com";

    public YomuMangasScraper() : this(null, null) { }

    public YomuMangasScraper(LogService? logService) : this(logService, null) { }

    public YomuMangasScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://yomumangas.com", logService, cfStore)
    {
        Http.DefaultRequestHeaders.Remove("Accept");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var apiUrl = $"{ApiUrl}{new Uri(url).AbsolutePath}".TrimEnd('/');
        var payload = await GetJsonAsync(apiUrl, ct);
        var manga = payload.GetProperty("manga");
        return new Manga
        {
            Name = GetString(manga, "title"),
            CoverUrl = BuildS3Image(GetString(manga, "cover")),
            Description = GetString(manga, "description"),
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var details = await GetJsonAsync($"{ApiUrl}{new Uri(url).AbsolutePath}".TrimEnd('/'), ct);
        var manga = details.GetProperty("manga");
        var mangaId = manga.GetProperty("id").GetInt32();
        var chaptersPayload = await GetJsonAsync($"{ApiUrl}/mangas/{mangaId}/chapters", ct);
        if (!chaptersPayload.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
            return [];

        return chapters.EnumerateArray()
            .Select(chapter =>
            {
                var number = GetDouble(chapter, "chapter");
                return new Chapter
                {
                    Title = $"Capítulo {number.ToString(System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')}",
                    Number = number,
                    Url = $"{url.TrimEnd('/')}/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                };
            })
            .OrderByDescending(chapter => chapter.Number)
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        return document.QuerySelectorAll("[class*=reader_Pages] img")
            .Select((element, index) => new Page
            {
                Number = index + 1,
                ImageUrl = ExtractImageSource(element, chapter.Url) ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }

    private static string BuildS3Image(string cover)
        => string.IsNullOrWhiteSpace(cover) ? string.Empty : $"https://s3.yomumangas.com/images/{cover[(cover.IndexOf("//", StringComparison.Ordinal) + 2)..]}";

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var node) ? node.GetString() ?? string.Empty : string.Empty;

    private static double GetDouble(JsonElement element, string property)
        => element.TryGetProperty(property, out var node) && double.TryParse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0d;
}
