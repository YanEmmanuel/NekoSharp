using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Taiyo;

public sealed partial class TaiyoScraper : HtmlScraperBase
{
    private const string ImageCdn = "https://cdn.taiyo.moe/medias";

    public override string Name => "Taiyō";

    protected override IReadOnlyCollection<string> SupportedHosts => ["taiyo.moe", "www.taiyo.moe"];

    public TaiyoScraper() : this(null, null) { }

    public TaiyoScraper(LogService? logService) : this(logService, null) { }

    public TaiyoScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://taiyo.moe", logService, cfStore)
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var mediaUrl = NormalizeMediaUrl(url);
        var document = await LoadDocumentAsync(mediaUrl, ct);
        var media = ExtractEmbeddedJson(document, "media", ",\\\"trackers\\\"", "}");

        var mediaId = GetString(media, "id");
        var coverId = GetString(media, "mainCoverId");
        var title = document.QuerySelector("p.media-title")?.TextContent?.Trim();

        if (string.IsNullOrWhiteSpace(title))
            title = PickTitle(media) ?? string.Empty;

        return new Manga
        {
            Name = title,
            CoverUrl = GetCoverUrl(document, mediaId, coverId),
            Description = BuildDescription(document, media),
            Url = $"{BaseUrl}/media/{ExtractMediaId(mediaUrl)}",
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var mediaId = ExtractMediaId(url);
        if (string.IsNullOrWhiteSpace(mediaId))
            throw new ArgumentException("URL invalida do Taiyo.", nameof(url));

        var chapters = new List<Chapter>();
        var page = 1;
        var totalPages = 1;

        do
        {
            var input = JsonSerializer.Serialize(new
            {
                _0 = new
                {
                    json = new
                    {
                        mediaId,
                        page,
                        perPage = 50
                    }
                }
            }).Replace("\"_0\"", "\"0\"", StringComparison.Ordinal);

            var apiUrl = $"{BaseUrl}/api/trpc/chapters.getByMediaId?batch=1&input={Uri.EscapeDataString(input)}";
            var response = await GetStringAsync(apiUrl, ct);
            var chapterList = ExtractChapterListPayload(response);

            if (!chapterList.TryGetProperty("chapters", out var chapterItems) || chapterItems.ValueKind != JsonValueKind.Array)
                break;

            totalPages = GetInt(chapterList, "totalPages") ?? totalPages;

            foreach (var item in chapterItems.EnumerateArray())
            {
                var chapterId = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(chapterId))
                    continue;

                var number = GetDouble(item, "number") ?? 0d;
                var rawTitle = GetString(item, "title");
                var title = string.IsNullOrWhiteSpace(rawTitle)
                    ? $"Capítulo {FormatNumber(number)}"
                    : rawTitle;

                chapters.Add(new Chapter
                {
                    Title = title,
                    Number = number,
                    Url = $"{BaseUrl}/chapter/{chapterId}/1"
                });
            }

            page++;
        } while (page <= totalPages);

        return chapters
            .OrderByDescending(chapter => chapter.Number)
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        var chapterObj = ExtractEmbeddedJson(document, "mediaChapter", ",\\\"chapters\\\"", "}}");
        var chapterId = GetString(chapterObj, "id");

        if (string.IsNullOrWhiteSpace(chapterId) ||
            !chapterObj.TryGetProperty("media", out var media) ||
            !chapterObj.TryGetProperty("pages", out var pagesNode) ||
            pagesNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var mediaId = GetString(media, "id");
        var baseUrl = $"{ImageCdn}/{mediaId}/chapters/{chapterId}";
        var pages = new List<Page>();
        var number = 1;

        foreach (var pageNode in pagesNode.EnumerateArray())
        {
            var pageId = GetString(pageNode, "id");
            if (string.IsNullOrWhiteSpace(pageId))
                continue;

            pages.Add(new Page
            {
                Number = number++,
                ImageUrl = $"{baseUrl}/{pageId}.jpg",
                RefererUrl = chapter.Url
            });
        }

        return pages;
    }

    private static string NormalizeMediaUrl(string url)
    {
        if (Guid.TryParse(url, out var id))
            return $"https://taiyo.moe/media/{id:D}";

        return url;
    }

    private static string ExtractMediaId(string url)
    {
        if (Guid.TryParse(url, out var directId))
            return directId.ToString();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;

        var segments = uri.Segments
            .Select(segment => segment.Trim('/'))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        var mediaIndex = Array.FindIndex(segments, segment => segment.Equals("media", StringComparison.OrdinalIgnoreCase));
        return mediaIndex >= 0 && mediaIndex + 1 < segments.Length ? segments[mediaIndex + 1] : string.Empty;
    }

    private static JsonElement ExtractChapterListPayload(string response)
    {
        using var doc = JsonDocument.Parse(response);
        if (TryFindChapterList(doc.RootElement, out var found))
            return found.Clone();

        var match = ChapterPayloadRegex().Match(response);
        if (match.Success)
            return ParseJson(match.Groups[1].Value);

        throw new InvalidOperationException("Nao foi possivel ler a lista de capitulos do Taiyo.");
    }

    private static bool TryFindChapterList(JsonElement element, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("chapters", out var chapters) &&
            chapters.ValueKind == JsonValueKind.Array &&
            element.TryGetProperty("totalPages", out _))
        {
            found = element;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (TryFindChapterList(property.Value, out found))
                    return true;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindChapterList(item, out found))
                    return true;
            }
        }

        found = default;
        return false;
    }

    private static JsonElement ExtractEmbeddedJson(IDocument document, string itemName, string terminator, string suffix)
    {
        var marker = $",{{\\\"{itemName}\\\":";

        foreach (var script in document.Scripts)
        {
            var data = script.TextContent;
            var start = data.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                continue;

            var json = data[(start + marker.Length)..];
            var end = json.IndexOf(terminator, StringComparison.Ordinal);
            if (end < 0)
                continue;

            json = json[..end] + suffix;
            json = UnescapeNextJson(json);
            return ParseJson(json);
        }

        return default;
    }

    private static string UnescapeNextJson(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next is '"' or '\\' or '/')
                {
                    builder.Append(next);
                    i++;
                    continue;
                }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static string GetCoverUrl(IDocument document, string mediaId, string coverId)
    {
        var image = document.QuerySelectorAll("section img")
            .Select(img => NormalizeImageSource(img.GetAttribute("srcset") ?? img.GetAttribute("src")))
            .FirstOrDefault(src => !string.IsNullOrWhiteSpace(src));

        if (!string.IsNullOrWhiteSpace(image))
            return image!;

        return string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(coverId)
            ? string.Empty
            : $"https://taiyo.moe/_next/image?url={ImageCdn}/{mediaId}/covers/{coverId}.jpg&w=256&q=75";
    }

    private static string BuildDescription(IDocument document, JsonElement media)
    {
        var parts = new List<string>();
        var synopsis = document.QuerySelector("section > div.flex + div p")?.TextContent?.Trim();
        if (string.IsNullOrWhiteSpace(synopsis))
            synopsis = GetString(media, "synopsis");

        if (!string.IsNullOrWhiteSpace(synopsis))
            parts.Add(synopsis);

        var genres = GetGenres(media);
        if (!string.IsNullOrWhiteSpace(genres))
            parts.Add($"Gêneros: {genres}");

        var status = GetString(media, "status");
        if (!string.IsNullOrWhiteSpace(status))
            parts.Add($"Status: {TranslateStatus(status)}");

        var alternativeTitles = GetAlternativeTitles(media);
        if (!string.IsNullOrWhiteSpace(alternativeTitles))
            parts.Add($"Títulos alternativos:\n{alternativeTitles}");

        return string.Join("\n\n", parts);
    }

    private static string? PickTitle(JsonElement media)
    {
        if (!media.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? best = null;
        foreach (var title in titles.EnumerateArray())
        {
            var language = GetString(title, "language");
            if (language.Contains("en", StringComparison.OrdinalIgnoreCase))
                return GetString(title, "title");

            if (best is null || (GetInt(title, "priority") ?? 0) > (GetInt(best.Value, "priority") ?? 0))
                best = title;
        }

        return best is null ? null : GetString(best.Value, "title");
    }

    private static string GetAlternativeTitles(JsonElement media)
    {
        if (!media.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array)
            return string.Empty;

        return string.Join('\n', titles.EnumerateArray()
            .Select(title =>
            {
                var name = GetString(title, "title");
                var language = GetString(title, "language").Split('_', 2)[0];
                return string.IsNullOrWhiteSpace(name) ? null : $"{language}: {name}";
            })
            .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string GetGenres(JsonElement media)
    {
        if (!media.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
            return string.Empty;

        return string.Join(", ", genres.EnumerateArray()
            .Select(genre => genre.GetString())
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Select(TranslateGenre));
    }

    private static string TranslateGenre(string? genre)
        => genre switch
        {
            "ACTION" => "Ação",
            "ADVENTURE" => "Aventura",
            "COMEDY" => "Comédia",
            "DRAMA" => "Drama",
            "ECCHI" => "Ecchi",
            "FANTASY" => "Fantasia",
            "HENTAI" => "Hentai",
            "HORROR" => "Horror",
            "MAHOU_SHOUJO" => "Mahou Shoujo",
            "MECHA" => "Mecha",
            "MUSIC" => "Música",
            "MYSTERY" => "Mistério",
            "PSYCHOLOGICAL" => "Psicológico",
            "ROMANCE" => "Romance",
            "SCI_FI" => "Sci-fi",
            "SLICE_OF_LIFE" => "Slice of Life",
            "SPORTS" => "Esportes",
            "SUPERNATURAL" => "Sobrenatural",
            "THRILLER" => "Thriller",
            _ => genre ?? string.Empty
        };

    private static string TranslateStatus(string status)
        => status switch
        {
            "FINISHED" => "Completo",
            "RELEASING" => "Em lançamento",
            _ => status
        };

    private static string FormatNumber(double number)
        => number.ToString("0.###", CultureInfo.InvariantCulture);

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return string.Empty;

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        var raw = GetString(element, propertyName);
        return int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        var raw = GetString(element, propertyName);
        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    [GeneratedRegex(@"(\{""chapters"".+?""totalPages"":\d+\})", RegexOptions.Singleline)]
    private static partial Regex ChapterPayloadRegex();
}
