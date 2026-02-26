using System.Globalization;
using System.Net;
using System.Text.Json;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MediocreScan;

public sealed class MediocreScanScraper : IScraper, IInteractiveAuthProvider
{
    public string Name => "MediocreScan";
    public string BaseUrl => _authProfile.SiteBaseUrl;

    private readonly HttpClient _http;
    private readonly LogService? _log;
    private readonly ProviderAuthProfile _authProfile;
    private readonly ProviderAuthService _authService;

    public MediocreScanScraper() : this(null, null) { }

    public MediocreScanScraper(LogService? logService) : this(logService, null) { }

    public MediocreScanScraper(LogService? logService, CloudflareCredentialStore? _)
    {
        _log = logService;
        _authProfile = ProviderAuthProfile.CreateMediocreScan();

        var authStore = new ProviderAuthStore(logService: logService);
        var browserFlow = new ProviderAuthBrowserFlow(_authProfile, logService: logService);
        _authService = new ProviderAuthService(_authProfile, authStore, browserFlow, logService: logService);

        var inner = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        HttpMessageHandler handler = new ProviderAuthHandler(_authService, _authProfile, logService, inner);
        if (logService is not null)
            handler = new LoggingHttpHandler(logService, handler);

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_authProfile.ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    public bool CanHandle(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals("mediocrescan.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("www.mediocrescan.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var parsed = ParseSupportedUrl(url);
        var obraId = parsed.Kind switch
        {
            MediocreUrlKind.Obra => parsed.Id,
            MediocreUrlKind.Capitulo => await ResolveObraIdFromChapterAsync(parsed.Id, ct),
            _ => throw new ArgumentException("URL do MediocreScan inválida. Use /obra/{id} ou /capitulo/{id}.", nameof(url))
        };

        var obra = await GetJsonAsync($"obras/{obraId}", ct);

        var name = GetString(obra, "nome") ?? $"Obra {obraId}";
        var description = GetString(obra, "sinopse") ?? GetString(obra, "descricao") ?? string.Empty;
        var coverUrl = BuildCoverUrl(obraId, GetString(obra, "capa"));

        _log?.Debug($"[MediocreScan] Resolved obra={obraId} for manga info");

        return new Manga
        {
            Name = name,
            CoverUrl = coverUrl,
            Description = description,
            Url = $"{BaseUrl}/obra/{obraId}",
            SiteName = Name
        };
    }

    public async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var parsed = ParseSupportedUrl(url);
        var obraId = parsed.Kind switch
        {
            MediocreUrlKind.Obra => parsed.Id,
            MediocreUrlKind.Capitulo => await ResolveObraIdFromChapterAsync(parsed.Id, ct),
            _ => throw new ArgumentException("URL do MediocreScan inválida. Use /obra/{id} ou /capitulo/{id}.", nameof(url))
        };

        _log?.Info($"[MediocreScan] Loading chapters for obra={obraId}");

        const int limit = 100;
        var page = 1;
        var scannedPages = 0;
        var byId = new Dictionary<int, Chapter>();

        while (scannedPages < 200)
        {
            ct.ThrowIfCancellationRequested();
            scannedPages++;

            var payload = await GetJsonAsync($"capitulos?obr_id={obraId}&page={page}&limite={limit}&order=desc", ct);

            if (!payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            var count = 0;
            foreach (var item in data.EnumerateArray())
            {
                var chapterId = GetInt(item, "id");
                if (!chapterId.HasValue || chapterId.Value <= 0)
                    continue;

                count++;
                byId[chapterId.Value] = MapChapter(item, chapterId.Value);
            }

            if (count == 0)
                break;

            var hasNext = false;
            if (payload.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object)
            {
                var totalPages = GetInt(pagination, "totalPages");
                hasNext = totalPages.HasValue ? page < totalPages.Value : count >= limit;
            }
            else
            {
                hasNext = count >= limit;
            }

            if (!hasNext)
                break;

            page++;
        }

        var chapters = byId
            .OrderBy(x => x.Value.Number)
            .ThenBy(x => x.Key)
            .Select(x => x.Value)
            .ToList();

        _log?.Info($"[MediocreScan] Loaded {chapters.Count} chapters for obra={obraId}");
        return chapters;
    }

    public async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        if (!TryResolveChapterId(chapter, out var chapterId))
            throw new ArgumentException("Capítulo inválido para MediocreScan. URL esperada: /capitulo/{id}.", nameof(chapter));

        _log?.Debug($"[MediocreScan] Loading pages for chapter={chapterId}");

        var payload = await GetJsonAsync($"capitulos/{chapterId}", ct);
        var obraId = TryGetObraId(payload);
        if (!obraId.HasValue || obraId.Value <= 0)
        {
            obraId = await ResolveObraIdFromChapterAsync(chapterId, ct);
        }

        if (!payload.TryGetProperty("paginas", out var pagesJson) || pagesJson.ValueKind != JsonValueKind.Array)
            return [];

        var chapterFolder = ResolveChapterFolder(payload, chapterId);
        var pages = new List<Page>();
        var index = 1;

        foreach (var pageItem in pagesJson.EnumerateArray())
        {
            var src = GetString(pageItem, "src");
            if (string.IsNullOrWhiteSpace(src))
                continue;

            pages.Add(new Page
            {
                Number = index++,
                ImageUrl = BuildPageImageUrl(obraId.Value, chapterFolder, src)
            });
        }

        return pages;
    }

    public Task<AuthSessionState> GetAuthStateAsync(CancellationToken ct = default)
        => _authService.GetAuthStateAsync(ct);

    public Task<AuthSessionState> LoginInteractivelyAsync(CancellationToken ct = default)
        => _authService.LoginInteractivelyAsync(ct);

    public Task ClearAuthAsync(CancellationToken ct = default)
        => _authService.ClearAuthAsync(ct);

    private async Task<int> ResolveObraIdFromChapterAsync(int chapterId, CancellationToken ct)
    {
        var chapter = await GetJsonAsync($"capitulos/{chapterId}", ct);

        if (chapter.TryGetProperty("obra", out var obra) && obra.ValueKind == JsonValueKind.Object)
        {
            var obraId = GetInt(obra, "id");
            if (obraId.HasValue && obraId.Value > 0)
                return obraId.Value;
        }

        throw new InvalidOperationException($"Não foi possível resolver obra.id para capítulo {chapterId}.");
    }

    private async Task<JsonElement> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        using var response = await _http.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Mediocre API retornou {(int)response.StatusCode} ({response.ReasonPhrase}) para '{relativeUrl}'. Body: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    internal static MediocreUrlRef ParseSupportedUrl(string url)
    {
        if (!MediocreUrlParser.TryParse(url, out var parsed))
            throw new ArgumentException("URL do MediocreScan inválida. Use /obra/{id} ou /capitulo/{id}.", nameof(url));

        if (parsed.Kind != MediocreUrlKind.Obra && parsed.Kind != MediocreUrlKind.Capitulo)
            throw new ArgumentException("URL do MediocreScan inválida. Use /obra/{id} ou /capitulo/{id}.", nameof(url));

        return parsed;
    }

    private static bool TryResolveChapterId(Chapter chapter, out int chapterId)
    {
        chapterId = 0;

        if (MediocreUrlParser.TryParse(chapter.Url, out var parsed) && parsed.Kind == MediocreUrlKind.Capitulo)
        {
            chapterId = parsed.Id;
            return true;
        }

        return false;
    }

    private static Chapter MapChapter(JsonElement chapterJson, int chapterId)
    {
        var title = GetString(chapterJson, "nome") ?? $"Capítulo {chapterId}";

        var number = GetDouble(chapterJson, "numero");
        if (!number.HasValue)
            number = ChapterHelper.ExtractChapterNumber(title);

        return new Chapter
        {
            Title = title,
            Number = number ?? 0,
            Url = $"https://mediocrescan.com/capitulo/{chapterId}"
        };
    }

    private static string BuildCoverUrl(int obraId, string? coverName)
    {
        if (string.IsNullOrWhiteSpace(coverName))
            return string.Empty;

        if (Uri.TryCreate(coverName, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var clean = coverName.TrimStart('/');
        return $"https://cdn.mediocretoons.site/obras/{obraId}/{clean}";
    }

    private static string BuildPageImageUrl(int obraId, string chapterFolder, string src)
    {
        if (Uri.TryCreate(src, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var clean = src.TrimStart('/');
        return $"https://cdn.mediocrescan.com/obras/{obraId}/capitulos/{chapterFolder}/{clean}";
    }

    private static int? TryGetObraId(JsonElement chapterPayload)
    {
        if (chapterPayload.TryGetProperty("obra", out var obra) && obra.ValueKind == JsonValueKind.Object)
        {
            var obraId = GetInt(obra, "id");
            if (obraId.HasValue && obraId.Value > 0)
                return obraId.Value;
        }

        return null;
    }

    private static string ResolveChapterFolder(JsonElement chapterPayload, int chapterId)
    {
        var raw = GetString(chapterPayload, "numero");
        if (string.IsNullOrWhiteSpace(raw))
            return chapterId.ToString(CultureInfo.InvariantCulture);

        raw = raw.Trim().Replace(',', '.');

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return raw;

        if (Math.Abs(number - Math.Round(number)) < 0.000001d)
            return ((int)Math.Round(number)).ToString(CultureInfo.InvariantCulture);

        return number.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        var raw = GetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        var raw = GetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
