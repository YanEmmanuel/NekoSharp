using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Comix;

public sealed class ComixScraper : IScraper
{
    public string Name => "Comix";
    public string BaseUrl => "https://comix.to";

    private const string ApiBaseUrl = "https://comix.to/api/v2/";
    private const int OfficialScanlationGroupId = 9275;

    private readonly HttpClient _http;
    private readonly LogService? _log;

    public ComixScraper() : this(null, null) { }

    public ComixScraper(LogService? logService) : this(logService, null) { }

    public ComixScraper(LogService? logService, CloudflareCredentialStore? cfStore)
    {
        _log = logService;

        var inner = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli
        };

        HttpMessageHandler handler = new CloudflareHandler(
            inner: inner,
            logService: logService,
            store: cfStore);

        if (logService is not null)
            handler = new LoggingHttpHandler(logService, handler);

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(45)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
        _http.DefaultRequestHeaders.Referrer = new Uri($"{BaseUrl}/");
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public bool CanHandle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals("comix.to", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("www.comix.to", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var parsed = ParseSupportedUrl(url);
        var result = await GetResultAsync(
            $"manga/{Uri.EscapeDataString(parsed.HashId)}" +
            "?includes[]=demographic" +
            "&includes[]=genre" +
            "&includes[]=theme" +
            "&includes[]=author" +
            "&includes[]=artist" +
            "&includes[]=publisher",
            ct);

        var title = GetString(result, "title") ?? $"Comix {parsed.HashId}";
        var slug = GetString(result, "slug");
        var coverUrl =
            GetNestedString(result, "poster", "large") ??
            GetNestedString(result, "poster", "medium") ??
            GetNestedString(result, "poster", "small") ??
            string.Empty;

        return new Manga
        {
            Name = title,
            CoverUrl = coverUrl,
            Description = BuildDescription(result),
            Url = BuildMangaUrl(parsed.HashId, slug),
            SiteName = Name
        };
    }

    public async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var parsed = ParseSupportedUrl(url);
        const int limit = 100;
        var page = 1;
        var bestByKey = new Dictionary<string, ComixChapterCandidate>(StringComparer.Ordinal);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var result = await GetResultAsync(
                $"manga/{Uri.EscapeDataString(parsed.HashId)}/chapters" +
                $"?order%5Bnumber%5D=desc&limit={limit}&page={page}",
                ct);

            if (!result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                break;

            var itemCount = 0;
            foreach (var item in items.EnumerateArray())
            {
                itemCount++;
                var candidate = ParseChapterCandidate(item);
                if (candidate.ChapterId <= 0)
                    continue;

                var key = BuildChapterDedupKey(candidate);
                if (!bestByKey.TryGetValue(key, out var current) || IsBetterChapter(candidate, current))
                    bestByKey[key] = candidate;
            }

            if (itemCount == 0 || !HasNextPage(result))
                break;

            page++;
        }

        var chapters = bestByKey.Values
            .OrderByDescending(static chapter => chapter.Number)
            .ThenByDescending(static chapter => chapter.UpdatedAt)
            .ThenByDescending(static chapter => chapter.ChapterId)
            .Select(chapter => new Chapter
            {
                Number = chapter.Number,
                Title = BuildChapterTitle(chapter),
                Url = BuildChapterUrl(parsed.MangaSegment, chapter.ChapterId)
            })
            .ToList();

        _log?.Info($"[Comix] Loaded {chapters.Count} chapters for manga={parsed.HashId}");
        return chapters;
    }

    public async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var parsed = ParseSupportedUrl(chapter.Url);
        if (parsed.Kind != ComixUrlKind.Chapter || parsed.ChapterId <= 0)
            throw new ArgumentException("Capítulo inválido do Comix. Use uma URL no formato /title/<hash>/<chapterId>.", nameof(chapter));

        var result = await GetResultAsync($"chapters/{parsed.ChapterId}", ct);
        if (!result.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Capítulo {parsed.ChapterId} não possui imagens.");

        var pages = new List<Page>();
        var pageNumber = 1;

        foreach (var image in images.EnumerateArray())
        {
            var imageUrl = GetString(image, "url");
            if (string.IsNullOrWhiteSpace(imageUrl))
                continue;

            pages.Add(new Page
            {
                Number = pageNumber++,
                ImageUrl = imageUrl,
                RefererUrl = chapter.Url
            });
        }

        if (pages.Count == 0)
            throw new InvalidOperationException($"Capítulo {parsed.ChapterId} não possui imagens válidas.");

        return pages;
    }

    private async Task<JsonElement> GetResultAsync(string relativeUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        using var response = await _http.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Comix API retornou {(int)response.StatusCode} ({response.ReasonPhrase}) para '{relativeUrl}'. Body: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("result", out var result) ||
            result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(
                $"Resposta inválida da API do Comix para '{relativeUrl}'. {ExtractApiMessage(root)}");
        }

        return result.Clone();
    }

    internal static ComixUrlRef ParseSupportedUrl(string url)
    {
        if (!ComixUrlParser.TryParse(url, out var parsed))
            throw new ArgumentException("URL do Comix inválida. Use /title/<hash>-slug ou /title/<hash>-slug/<chapterId>-slug.", nameof(url));

        return parsed;
    }

    private static string BuildMangaUrl(string hashId, string? slug)
    {
        var slugPart = string.IsNullOrWhiteSpace(slug)
            ? hashId
            : $"{hashId}-{slug.Trim().Trim('/')}";

        return $"https://comix.to/title/{slugPart}";
    }

    private static string BuildChapterUrl(string mangaSegment, int chapterId)
        => $"https://comix.to/title/{mangaSegment.Trim('/')}/{chapterId}";

    private static string BuildDescription(JsonElement manga)
    {
        var sections = new List<string>();

        var synopsis = GetString(manga, "synopsis");
        if (!string.IsNullOrWhiteSpace(synopsis))
            sections.Add(synopsis);

        var altTitles = GetStringArray(manga, "alt_titles");
        if (altTitles.Count > 0)
            sections.Add("Alternative Names:\n" + string.Join('\n', altTitles));

        var metadata = new List<string>();

        var year = GetInt(manga, "year") ?? GetInt(manga, "start_date");
        if (year.HasValue && year.Value > 0)
            metadata.Add($"Year: {year.Value}");

        var type = FormatType(GetString(manga, "type"));
        if (!string.IsNullOrWhiteSpace(type))
            metadata.Add($"Type: {type}");

        var status = FormatStatus(GetString(manga, "status"));
        if (!string.IsNullOrWhiteSpace(status))
            metadata.Add($"Status: {status}");

        var authors = GetTermTitles(manga, "author");
        if (authors.Count > 0)
            metadata.Add($"Author: {string.Join(", ", authors)}");

        var artists = GetTermTitles(manga, "artist");
        if (artists.Count > 0)
            metadata.Add($"Artist: {string.Join(", ", artists)}");

        var demographics = GetTermTitles(manga, "demographic");
        if (demographics.Count > 0)
            metadata.Add($"Demographics: {string.Join(", ", demographics)}");

        var genres = GetTermTitles(manga, "genre");
        if (genres.Count > 0)
            metadata.Add($"Genres: {string.Join(", ", genres)}");

        var themes = GetTermTitles(manga, "theme");
        if (themes.Count > 0)
            metadata.Add($"Themes: {string.Join(", ", themes)}");

        var publishers = GetTermTitles(manga, "publisher");
        if (publishers.Count > 0)
            metadata.Add($"Publisher: {string.Join(", ", publishers)}");

        var score = GetDouble(manga, "rated_avg");
        if (score.HasValue && score.Value > 0)
            metadata.Add($"Score: {score.Value.ToString("0.##", CultureInfo.InvariantCulture)}/10");

        if (GetBooleanishInt(manga, "is_nsfw") == 1)
            metadata.Add("NSFW: Yes");

        if (metadata.Count > 0)
            sections.Add(string.Join(Environment.NewLine, metadata));

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static string BuildChapterTitle(ComixChapterCandidate chapter)
    {
        if (chapter.Number <= 0)
            return string.IsNullOrWhiteSpace(chapter.Name) ? $"Chapter {FormatChapterNumber(chapter.Number)}" : chapter.Name;

        var title = $"Chapter {FormatChapterNumber(chapter.Number)}";
        return string.IsNullOrWhiteSpace(chapter.Name) ? title : $"{title}: {chapter.Name}";
    }

    private static string FormatChapterNumber(double number)
        => number.ToString("0.####################", CultureInfo.InvariantCulture);

    private static string BuildChapterDedupKey(ComixChapterCandidate chapter)
    {
        var nameKey = NormalizeDedupName(chapter.Name);
        return $"{chapter.Number.ToString("0.####################", CultureInfo.InvariantCulture)}|{nameKey}";
    }

    private static string NormalizeDedupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        return string.Join(
            ' ',
            name.Trim()
                .ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsBetterChapter(ComixChapterCandidate candidate, ComixChapterCandidate current)
    {
        var officialCandidate = candidate.ScanlationGroupId == OfficialScanlationGroupId || candidate.IsOfficial == 1;
        var officialCurrent = current.ScanlationGroupId == OfficialScanlationGroupId || current.IsOfficial == 1;

        if (officialCandidate != officialCurrent)
            return officialCandidate;

        if (candidate.Votes != current.Votes)
            return candidate.Votes > current.Votes;

        return candidate.UpdatedAt >= current.UpdatedAt;
    }

    private static bool HasNextPage(JsonElement result)
    {
        if (!result.TryGetProperty("pagination", out var pagination) || pagination.ValueKind != JsonValueKind.Object)
            return false;

        var page = GetInt(pagination, "current_page") ?? GetInt(pagination, "page") ?? 1;
        var lastPage = GetInt(pagination, "last_page") ?? GetInt(pagination, "lastPage") ?? page;
        return page < lastPage;
    }

    private static ComixChapterCandidate ParseChapterCandidate(JsonElement item)
    {
        return new ComixChapterCandidate(
            ChapterId: GetInt(item, "chapter_id") ?? 0,
            Number: GetDouble(item, "number") ?? 0,
            Name: GetString(item, "name") ?? string.Empty,
            Votes: GetInt(item, "votes") ?? 0,
            UpdatedAt: GetLong(item, "updated_at") ?? GetLong(item, "created_at") ?? 0,
            ScanlationGroupId: GetInt(item, "scanlation_group_id") ?? 0,
            ScanlationGroupName: GetNestedString(item, "scanlation_group", "name") ?? string.Empty,
            IsOfficial: GetBooleanishInt(item, "is_official"));
    }

    private static string ExtractApiMessage(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString() ?? string.Empty;

        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in messages.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                    return entry.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        var values = new List<string>();
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in property.EnumerateArray())
        {
            var value = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.ToString(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value.Trim());
        }

        return values;
    }

    private static List<string> GetTermTitles(JsonElement element, string propertyName)
    {
        var values = new List<string>();
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in property.EnumerateArray())
        {
            var title = GetString(item, "title");
            if (!string.IsNullOrWhiteSpace(title))
                values.Add(title);
        }

        return values;
    }

    private static string? GetNestedString(JsonElement element, string objectProperty, string nestedProperty)
    {
        if (!element.TryGetProperty(objectProperty, out var child) || child.ValueKind != JsonValueKind.Object)
            return null;

        return GetString(child, nestedProperty);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };
    }

    private static long? GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static int GetBooleanishInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        return property.ValueKind switch
        {
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.Number when property.TryGetInt32(out var value) => value != 0 ? 1 : 0,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var flag) => flag ? 1 : 0,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value != 0 ? 1 : 0,
            _ => 0
        };
    }

    private static string? FormatType(string? type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "manga" => "Manga",
            "manhwa" => "Manhwa",
            "manhua" => "Manhua",
            "other" => "Other",
            null or "" => null,
            var value => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '))
        };
    }

    private static string? FormatStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "releasing" => "Releasing",
            "on_hiatus" => "On Hiatus",
            "finished" => "Finished",
            "discontinued" => "Discontinued",
            "not_yet_released" => "Not Yet Released",
            null or "" => null,
            var value => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '))
        };
    }

    private readonly record struct ComixChapterCandidate(
        int ChapterId,
        double Number,
        string Name,
        int Votes,
        long UpdatedAt,
        int ScanlationGroupId,
        string ScanlationGroupName,
        int IsOfficial);
}
