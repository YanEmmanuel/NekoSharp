using System.Globalization;
using System.Net;
using System.Text.Json;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MediocreScan;

public sealed class MediocreScanScraper : IScraper, ICredentialAuthProvider
{
    private const string CdnBaseUrl = "https://cdn.mediocrescan.com";

    public string Name => "Mediocre Scan";
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
               uri.Host.Equals("www.mediocrescan.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("back.mediocrescan.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("api.mediocretoons.site", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("api.mediocretoons.net", StringComparison.OrdinalIgnoreCase);
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

        var name = GetString(obra, "nome", "obr_nome") ?? $"Obra {obraId}";
        var description = GetString(obra, "sinopse", "descricao", "obr_descricao") ?? string.Empty;
        var coverUrl = BuildCoverUrl(obraId, GetString(obra, "imagem", "obr_imagem"));

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
        var expectedTotal = 0;

        while (scannedPages < 200)
        {
            ct.ThrowIfCancellationRequested();
            scannedPages++;

            JsonElement payload;
            try
            {
                payload = await GetJsonAsync($"capitulos?obr_id={obraId}&page={page}&limite={limit}&order=desc", ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _log?.Warn($"[MediocreScan] Endpoint paginado de capítulos retornou 404 para obra={obraId}. Usando fallback do payload da obra.");
                break;
            }

            if (!payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            var count = MergeChapterArray(data, byId);
            expectedTotal = Math.Max(expectedTotal, GetExpectedChapterCount(payload));

            if (count == 0)
                break;

            if (!HasNextChapterPage(payload, page, count, limit))
                break;

            page++;
        }

        if (byId.Count == 0 || expectedTotal > byId.Count)
        {
            var obraPayload = await GetJsonAsync($"obras/{obraId}", ct);
            expectedTotal = Math.Max(expectedTotal, GetInt(obraPayload, "total_capitulos") ?? 0);

            if (obraPayload.TryGetProperty("capitulos", out var embeddedChapters) && embeddedChapters.ValueKind == JsonValueKind.Array)
            {
                var before = byId.Count;
                var added = MergeChapterArray(embeddedChapters, byId);
                if (added > 0)
                    _log?.Debug($"[MediocreScan] Added {byId.Count - before} missing chapter(s) from obra payload for obra={obraId}");
            }

            if (expectedTotal > byId.Count)
                _log?.Warn($"[MediocreScan] Chapter list for obra={obraId} is still incomplete after fallback ({byId.Count}/{expectedTotal})");
        }

        var chapters = byId
            .OrderByDescending(x => x.Value.Number)
            .ThenByDescending(x => x.Key)
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

        var chapterFolder = ResolveChapterFolder(payload, chapterId);
        if (payload.TryGetProperty("paginas", out var pagesJson) && pagesJson.ValueKind == JsonValueKind.Array)
        {
            var inlinePages = MapPageArray(pagesJson, obraId.Value, chapterFolder);
            if (inlinePages.Count > 0)
                return inlinePages;
        }

        var chapterUuid = GetString(payload, "cap_uuid", "uuid");
        if (string.IsNullOrWhiteSpace(chapterUuid))
            throw new InvalidOperationException($"Capítulo {chapterId} não trouxe páginas nem cap_uuid para consultar o CDN.");

        var manifestUrl = BuildChapterPagesManifestUrl(obraId.Value, chapterFolder, chapterUuid);
        var manifest = await GetJsonAsync(manifestUrl, ct);
        var pages = MapPageArray(manifest, obraId.Value, chapterFolder);

        var expectedPageCount = GetInt(payload, "cap_paginas_count", "paginas_count");
        if (expectedPageCount.HasValue && expectedPageCount.Value != pages.Count)
            _log?.Warn($"[MediocreScan] Capítulo {chapterId} informou {expectedPageCount} página(s), mas o CDN retornou {pages.Count}.");

        return pages;
    }

    public Task<AuthSessionState> GetAuthStateAsync(CancellationToken ct = default)
        => _authService.GetAuthStateAsync(ct);

    public Task<AuthSessionState> LoginInteractivelyAsync(CancellationToken ct = default)
        => _authService.LoginInteractivelyAsync(ct);

    public Task<AuthSessionState> LoginWithCredentialsAsync(
        string usernameOrEmail,
        string password,
        bool rememberCredentials = true,
        CancellationToken ct = default)
        => _authService.LoginWithCredentialsAsync(usernameOrEmail, password, rememberCredentials, ct);

    public Task<bool> HasSavedCredentialsAsync(CancellationToken ct = default)
        => _authService.HasSavedCredentialsAsync(ct);

    public Task ClearSavedCredentialsAsync(CancellationToken ct = default)
        => _authService.ClearSavedCredentialsAsync(ct);

    public Task ClearAuthAsync(CancellationToken ct = default)
        => _authService.ClearAuthAsync(ct);

    private async Task<int> ResolveObraIdFromChapterAsync(int chapterId, CancellationToken ct)
    {
        var chapter = await GetJsonAsync($"capitulos/{chapterId}", ct);

        if (chapter.TryGetProperty("obra", out var obra) && obra.ValueKind == JsonValueKind.Object)
        {
            var obraId = GetInt(obra, "id", "obr_id");
            if (obraId.HasValue && obraId.Value > 0)
                return obraId.Value;
        }

        var directObraId = GetInt(chapter, "obra_id", "obr_id", "cap_obr_id");
        if (directObraId.HasValue && directObraId.Value > 0)
            return directObraId.Value;

        throw new InvalidOperationException($"Não foi possível resolver obra.id para capítulo {chapterId}.");
    }

    private async Task<JsonElement> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        ApplyRequestHeaders(request);

        using var response = await _http.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Mediocre API retornou {(int)response.StatusCode} ({response.ReasonPhrase}) para '{relativeUrl}'. Body: {body}",
                inner: null,
                response.StatusCode);
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
        var title = GetString(chapterJson, "nome", "cap_nome") ?? $"Capítulo {chapterId}";

        var number = GetDouble(chapterJson, "numero", "cap_num");
        if (!number.HasValue)
            number = ChapterHelper.ExtractChapterNumber(title);

        return new Chapter
        {
            Title = title,
            Number = number ?? 0,
            Url = $"https://mediocrescan.com/capitulo/{chapterId}"
        };
    }

    internal static int MergeChapterArray(JsonElement chaptersJson, IDictionary<int, Chapter> chaptersById)
    {
        if (chaptersJson.ValueKind != JsonValueKind.Array)
            return 0;

        var merged = 0;
        foreach (var item in chaptersJson.EnumerateArray())
        {
            var chapterId = GetInt(item, "id", "cap_id");
            if (!chapterId.HasValue || chapterId.Value <= 0)
                continue;

            merged++;
            chaptersById[chapterId.Value] = MapChapter(item, chapterId.Value);
        }

        return merged;
    }

    internal static bool HasNextChapterPage(JsonElement payload, int requestedPage, int itemCount, int pageSize)
    {
        if (!payload.TryGetProperty("pagination", out var pagination) || pagination.ValueKind != JsonValueKind.Object)
            return itemCount >= pageSize;

        var hasNextPage = GetBool(pagination, "hasNextPage");
        if (hasNextPage.HasValue)
            return hasNextPage.Value;

        var currentPage = GetInt(pagination, "currentPage") ?? GetInt(pagination, "pagina_atual") ?? requestedPage;
        var totalPages = GetInt(pagination, "totalPages") ?? GetInt(pagination, "paginas");
        if (totalPages.HasValue)
            return currentPage < totalPages.Value;

        var totalItems = GetInt(pagination, "totalItems") ?? GetInt(pagination, "total");
        var itemsPerPage = GetInt(pagination, "itemsPerPage") ?? GetInt(pagination, "itens_por_pagina");
        if (totalItems.HasValue && itemsPerPage.HasValue && itemsPerPage.Value > 0)
            return currentPage * itemsPerPage.Value < totalItems.Value;

        return itemCount >= pageSize;
    }

    internal static int GetExpectedChapterCount(JsonElement payload)
    {
        if (!payload.TryGetProperty("pagination", out var pagination) || pagination.ValueKind != JsonValueKind.Object)
            return 0;

        return GetInt(pagination, "totalItems") ?? GetInt(pagination, "total") ?? 0;
    }

    internal static List<Page> MapPageArray(JsonElement pagesJson, int obraId, string chapterFolder)
    {
        if (pagesJson.ValueKind != JsonValueKind.Array)
            return [];

        var entries = new List<(int SortOrder, string ImageUrl)>();
        var index = 1;

        foreach (var pageItem in pagesJson.EnumerateArray())
        {
            var src = GetString(pageItem, "url", "src", "pag_src", "pag_imagem", "pag_arquivo", "arquivo", "imagem", "link");
            if (string.IsNullOrWhiteSpace(src))
            {
                index++;
                continue;
            }

            var sortOrder = GetInt(pageItem, "ordem", "order", "numero", "num") ?? index;
            entries.Add((sortOrder, BuildPageImageUrl(obraId, chapterFolder, src)));
            index++;
        }

        return entries
            .OrderBy(entry => entry.SortOrder)
            .Select((entry, pageIndex) => new Page
            {
                Number = pageIndex + 1,
                ImageUrl = entry.ImageUrl,
                RefererUrl = "https://mediocrescan.com/"
            })
            .ToList();
    }

    private static string BuildCoverUrl(int obraId, string? coverName)
    {
        if (string.IsNullOrWhiteSpace(coverName))
            return string.Empty;

        if (Uri.TryCreate(coverName, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var clean = coverName.TrimStart('/');
        return $"{CdnBaseUrl}/obras/{obraId}/{clean}";
    }

    private static string BuildPageImageUrl(int obraId, string chapterFolder, string src)
    {
        if (Uri.TryCreate(src, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var clean = src.TrimStart('/');
        if (clean.StartsWith("obras/", StringComparison.OrdinalIgnoreCase))
            return $"{CdnBaseUrl}/{clean}";

        return $"{CdnBaseUrl}/obras/{obraId}/capitulos/{chapterFolder}/{clean}";
    }

    private static string BuildChapterPagesManifestUrl(int obraId, string chapterFolder, string chapterUuid)
    {
        return $"{CdnBaseUrl}/obras/{obraId}/capitulos/{chapterFolder}/{Uri.EscapeDataString(chapterUuid)}.json";
    }

    private static int? TryGetObraId(JsonElement chapterPayload)
    {
        if (chapterPayload.TryGetProperty("obra", out var obra) && obra.ValueKind == JsonValueKind.Object)
        {
            var obraId = GetInt(obra, "id", "obr_id");
            if (obraId.HasValue && obraId.Value > 0)
                return obraId.Value;
        }

        var directObraId = GetInt(chapterPayload, "obra_id", "obr_id", "cap_obr_id");
        if (directObraId.HasValue && directObraId.Value > 0)
            return directObraId.Value;

        return null;
    }

    private static string ResolveChapterFolder(JsonElement chapterPayload, int chapterId)
    {
        var raw = GetString(chapterPayload, "numero", "cap_num");
        if (string.IsNullOrWhiteSpace(raw))
            return chapterId.ToString(CultureInfo.InvariantCulture);

        raw = raw.Trim().Replace(',', '.');

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return raw;

        if (Math.Abs(number - Math.Round(number)) < 0.000001d)
            return ((int)Math.Round(number)).ToString(CultureInfo.InvariantCulture);

        return number.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            var result = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }

        return null;
    }

    private static int? GetInt(JsonElement element, params string[] propertyNames)
    {
        var raw = GetString(element, propertyNames);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? GetDouble(JsonElement element, params string[] propertyNames)
    {
        var raw = GetString(element, propertyNames);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private void ApplyRequestHeaders(HttpRequestMessage request)
    {
        request.Headers.Remove("Accept");
        request.Headers.TryAddWithoutValidation("Accept", _authProfile.AcceptHeaderValue);

        request.Headers.Remove("Origin");
        request.Headers.TryAddWithoutValidation("Origin", _authProfile.OriginHeaderValue);

        request.Headers.Referrer = new Uri(_authProfile.RefererHeaderValue);

        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgentProvider.Default);

        request.Headers.Remove(_authProfile.XAppKeyHeaderName);
        request.Headers.TryAddWithoutValidation(_authProfile.XAppKeyHeaderName, _authProfile.XAppKeyHeaderValue);
    }
}
