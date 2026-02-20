using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MangaDex;

public sealed class MangaDexScraper : IScraper
{
    public string Name => "MangaDex";
    public string BaseUrl => "https://mangadex.org";
    
    public string ApiUrl => "https://api.mangadex.org";

    private readonly HttpClient _http;

    public MangaDexScraper() : this(null) { }

    public MangaDexScraper(LogService? logService)
    {
        HttpMessageHandler handler = logService != null
            ? new LoggingHttpHandler(logService, new HttpClientHandler())
            : new HttpClientHandler();

        _http = new HttpClient(handler);
        _http.BaseAddress = new Uri(ApiUrl);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
    }

    public bool CanHandle(string url)
    {
        return url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var mangaId = ExtractMangaId(url);
        if (string.IsNullOrWhiteSpace(mangaId))
            throw new ArgumentException("URL invalida do MangaDex.", nameof(url));

        var requestUri = new Uri($"manga/{mangaId}?includes%5B%5D=cover_art", UriKind.Relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"MangaDex retornou {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {errorBody}");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (payload.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Resposta vazia do MangaDex.");

        var data = payload.GetProperty("data");
        var attributes = data.GetProperty("attributes");

        var title = GetLocalizedString(attributes.GetProperty("title"));
        var description = GetLocalizedString(attributes.GetProperty("description"));

        var coverFileName = GetCoverFileName(data);
        var coverLink = string.IsNullOrWhiteSpace(coverFileName)
            ? string.Empty
            : $"{BaseUrl}/covers/{mangaId}/{coverFileName}";

        return new Manga
        {
            Name = title,
            Description = description,
            CoverUrl = coverLink,
            Url = url,
            SiteName = Name
        };
    }

    public async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var mangaId = ExtractMangaId(url);
        var chapters = new List<Chapter>();
        var offset = 0;
        const int limit = 100;

        while (true)
        {
            var response = await _http.GetFromJsonAsync<JsonElement>(
                $"{ApiUrl}/manga/{mangaId}/feed?translatedLanguage[]=pt-br" +
                         $"&limit={limit}" +
                         $"&includes[]=scanlation_group" +
                         $"&includes[]=user" +
                         $"&order[volume]=desc" +
                         $"&order[chapter]=desc" +
                         $"&offset={offset}" +
                         $"&contentRating[]=safe" +
                         $"&contentRating[]=suggestive" +
                         $"&contentRating[]=erotica" +
                         $"&contentRating[]=pornographic" +
                         $"&includeUnavailable=0", ct);
            
            var data = response.GetProperty("data");
            foreach (var chapterData in data.EnumerateArray())
            {
                var attrs = chapterData.GetProperty("attributes");
                var chapterId = chapterData.GetProperty("id").GetString()!;
                        
                var chapterNum = attrs.GetProperty("chapter").GetString();
                var chapterTitle = attrs.GetProperty("title").GetString() ?? string.Empty;
                        
                if (!double.TryParse(chapterNum, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var num))
                    num = 0;
                        
                chapters.Add(new Chapter
                {
                    Title = string.IsNullOrEmpty(chapterTitle) ? $"Capítulo {chapterNum}" : chapterTitle,
                    Number = num,
                    Url = $"{ApiUrl}/at-home/server/{chapterId}"
                });
            }
            var total = response.GetProperty("total").GetInt32();
            offset += limit;
            if (offset >= total) break;
        }
        return chapters;
    }

    public async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>(chapter.Url, ct);
                
        var baseUrl = response.GetProperty("baseUrl").GetString()!;
        var chapterData = response.GetProperty("chapter");
        var hash = chapterData.GetProperty("hash").GetString()!;
        var pageFiles = chapterData.GetProperty("data");
                
        var pages = new List<Page>();
        var pageNum = 1;
                
        foreach (var file in pageFiles.EnumerateArray())
        {
            var fileName = file.GetString()!;
            pages.Add(new Page
            {
                Number = pageNum++,
                ImageUrl = $"{baseUrl}/data/{hash}/{fileName}"
            });
        }
                
        return pages;
    }
    
    private static string GetLocalizedString(JsonElement element)
    {
        if (element.TryGetProperty("en", out var en))
            return en.GetString() ?? string.Empty;
        if (element.TryGetProperty("ja", out var ja))
            return ja.GetString() ?? string.Empty;
        if (element.TryGetProperty("ja-ro", out var jaRo))
            return jaRo.GetString() ?? string.Empty;

        foreach (var prop in element.EnumerateObject())
            return prop.Value.GetString() ?? string.Empty;

        return string.Empty;
    }

    private static string ExtractMangaId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (Guid.TryParse(url, out var directId))
            return directId.ToString();

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!segments[i].Equals("title", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Guid.TryParse(segments[i + 1], out var id))
                    return id.ToString();
            }
        }

        var match = Regex.Match(url, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (!match.Success)
            throw new ArgumentException("URL invalida do MangaDex.");

        return match.Value;
    }

    private static string GetCoverFileName(JsonElement data)
    {
        if (!data.TryGetProperty("relationships", out var relationships))
            return string.Empty;

        foreach (var rel in relationships.EnumerateArray())
        {
            if (!rel.TryGetProperty("type", out var type) || type.GetString() != "cover_art")
                continue;

            if (rel.TryGetProperty("attributes", out var attributes) &&
                attributes.TryGetProperty("fileName", out var fileName))
            {
                return fileName.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}