using System.Text.Json;
using System.Text.RegularExpressions;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class GreenShitScraper : HtmlScraperBase
{
    protected abstract string ApiUrl { get; }
    protected abstract string CdnUrl { get; }
    protected abstract string CdnApiUrl { get; }
    protected abstract string ScanId { get; }

    protected GreenShitScraper(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
        : base(baseUrl, logService, cfStore)
    {
        Http.DefaultRequestHeaders.Remove("Origin");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", BaseUrl);
        Http.DefaultRequestHeaders.Remove("scan-id");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("scan-id", ScanId);
        Http.DefaultRequestHeaders.Remove("Accept");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var mangaId = await ResolveMangaIdAsync(url, ct);
        var manga = await GetJsonAsync($"{ApiUrl}/obras/{mangaId}", ct);
        return MapManga(manga, $"{BaseUrl}/obra/{mangaId}", true);
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var mangaId = await ResolveMangaIdAsync(url, ct);
        var manga = await GetJsonAsync($"{ApiUrl}/obras/{mangaId}", ct);

        if (!manga.TryGetProperty("capitulos", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
            return [];

        return chapters.EnumerateArray()
            .Select(MapChapter)
            .Where(chapter => chapter is not null)
            .Cast<Chapter>()
            .OrderByDescending(chapter => chapter.Number)
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var chapterId = ParseId(chapter.Url, "capitulo");
        var payload = await GetJsonAsync($"{ApiUrl}/capitulos/{chapterId}", ct);

        if (!payload.TryGetProperty("cap_paginas", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return [];

        var manga = payload.TryGetProperty("obra", out var mangaNode) && mangaNode.ValueKind == JsonValueKind.Object
            ? mangaNode
            : default;

        var mangaId = manga.ValueKind == JsonValueKind.Object && manga.TryGetProperty("obr_id", out var obrId)
            ? obrId.GetInt32()
            : 0;
        var scanId = manga.ValueKind == JsonValueKind.Object && manga.TryGetProperty("scan_id", out var scanIdNode)
            ? scanIdNode.GetInt32()
            : 0;
        var chapterNumber = payload.TryGetProperty("cap_numero", out var numberNode) ? numberNode.ToString().TrimEnd('0').TrimEnd('.') : "0";

        var result = new List<Page>();
        var index = 1;

        foreach (var page in pages.EnumerateArray())
        {
            var src = page.TryGetProperty("src", out var srcNode) ? srcNode.GetString() : null;
            var mime = page.TryGetProperty("mime", out var mimeNode) ? mimeNode.GetString() : null;

            if (string.IsNullOrWhiteSpace(src))
                continue;

            result.Add(new Page
            {
                Number = index++,
                ImageUrl = BuildImageUrl($"/scans/{scanId}/obras/{mangaId}/capitulos/{chapterNumber}/", src, CdnUrl, mime)
            });
        }

        return result;
    }

    protected virtual Manga MapManga(JsonElement manga, string canonicalUrl, bool includeDescription)
    {
        var name = manga.TryGetProperty("obr_nome", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty;
        var image = manga.TryGetProperty("obr_imagem", out var imageNode) ? imageNode.GetString() : null;
        var description = includeDescription && manga.TryGetProperty("obr_descricao", out var descNode)
            ? StripHtml(descNode.GetString())
            : string.Empty;

        return new Manga
        {
            Name = name,
            CoverUrl = BuildImageUrl(
                $"/scans/{ScanId}/obras/{GetInt32(manga, "obr_id")}/",
                image,
                CdnApiUrl,
                width: 300),
            Description = description,
            Url = canonicalUrl,
            SiteName = Name
        };
    }

    protected virtual Chapter? MapChapter(JsonElement chapter)
    {
        var id = GetInt32(chapter, "cap_id");
        var title = chapter.TryGetProperty("cap_nome", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty;
        if (id <= 0 || string.IsNullOrWhiteSpace(title))
            return null;

        var number = 0d;
        if (chapter.TryGetProperty("cap_numero", out var numberNode))
            double.TryParse(numberNode.ToString(), System.Globalization.CultureInfo.InvariantCulture, out number);

        return new Chapter
        {
            Title = title,
            Number = number > 0 ? number : ChapterHelper.ExtractChapterNumber(title),
            Url = $"{BaseUrl}/capitulo/{id}"
        };
    }

    protected virtual async Task<int> ResolveMangaIdAsync(string url, CancellationToken ct)
    {
        if (url.Contains("/obra/", StringComparison.OrdinalIgnoreCase))
            return ParseId(url, "obra");

        if (url.Contains("/capitulo/", StringComparison.OrdinalIgnoreCase))
        {
            var chapterId = ParseId(url, "capitulo");
            var payload = await GetJsonAsync($"{ApiUrl}/capitulos/{chapterId}", ct);
            if (payload.TryGetProperty("obra", out var manga) && manga.TryGetProperty("obr_id", out var mangaId))
                return mangaId.GetInt32();
        }

        throw new ArgumentException($"URL inválida do {Name}.", nameof(url));
    }

    protected static string BuildImageUrl(string? path, string? src, string baseUrl, string? mime = null, int? width = null)
    {
        if (string.IsNullOrWhiteSpace(src))
            return string.Empty;

        if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return src;

        if (src.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("wp-content/", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("manga_", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("WP-manga", StringComparison.OrdinalIgnoreCase) ||
            mime is not null)
        {
            return src switch
            {
                _ when src.StartsWith("manga_", StringComparison.OrdinalIgnoreCase) =>
                    NormalizeSlashes($"{baseUrl}/wp-content/uploads/WP-manga/data/{src}"),
                _ when src.StartsWith("WP-manga", StringComparison.OrdinalIgnoreCase) =>
                    NormalizeSlashes($"{baseUrl}/wp-content/uploads/{src}"),
                _ when src.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase) =>
                    NormalizeSlashes($"{baseUrl}/wp-content/{src}"),
                _ when src.StartsWith("wp-content/", StringComparison.OrdinalIgnoreCase) =>
                    NormalizeSlashes($"{baseUrl}/{src}"),
                _ =>
                    NormalizeSlashes($"{baseUrl}/wp-content/uploads/WP-manga/data/{src.TrimStart('/')}")
            };
        }

        var query = width.HasValue ? $"?width={width.Value}" : string.Empty;
        var safePath = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace("//", "/", StringComparison.Ordinal).Trim('/').Trim();
        var safeSrc = src.Replace("//", "/", StringComparison.Ordinal).Trim('/').Trim();

        return NormalizeSlashes($"{baseUrl}/{safePath}/{safeSrc}{query}");
    }

    protected static int ParseId(string url, string segment)
    {
        var match = Regex.Match(url, $"/{Regex.Escape(segment)}/(\\d+)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static int GetInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var node) && node.TryGetInt32(out var value) ? value : 0;

    private static string NormalizeSlashes(string url)
        => Regex.Replace(url, "(?<!:)/{2,}", "/");
}
