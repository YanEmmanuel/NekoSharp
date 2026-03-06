using System.Text.Json;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class ZeroThemeScraper : HtmlScraperBase
{
    protected virtual string CdnUrl => $"https://cdn.{new Uri(BaseUrl).Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase)}";
    protected virtual string ImageLocation => "/images";

    protected string SourceLocation => $"{CdnUrl}{ImageLocation}";

    protected ZeroThemeScraper(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
        : base(baseUrl, logService, cfStore)
    {
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var payload = GetPagePayload(document);
        var manga = TryGetNestedElement(payload, "props", "comic_infos")
                    ?? TryGetNestedElement(payload, "props", "chapter")
                    ?? throw new InvalidOperationException("Dados da obra não encontrados.");

        return new Manga
        {
            Name = GetString(manga, "title"),
            CoverUrl = BuildImageUrl(GetString(manga, "cover")),
            Description = StripHtml(GetString(manga, "description")),
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var payload = GetPagePayload(document);
        var manga = TryGetNestedElement(payload, "props", "comic_infos")
                    ?? throw new InvalidOperationException("Capítulos não encontrados.");

        if (!manga.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
            return [];

        return chapters.EnumerateArray()
            .Select(chapter =>
            {
                var title = chapter.TryGetProperty("chapter_number", out var numberNode)
                    ? numberNode.ToString()
                    : string.Empty;
                var path = GetString(chapter, "chapter_path");
                return string.IsNullOrWhiteSpace(path)
                    ? null
                    : new Chapter
                    {
                        Title = title,
                        Url = ToAbsoluteUrl(url, path) ?? path,
                        Number = ChapterHelper.ExtractChapterNumber(title)
                    };
            })
            .Where(chapter => chapter is not null)
            .Cast<Chapter>()
            .ToList();
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        var payload = GetPagePayload(document);
        var chapterNode = TryGetNestedElement(payload, "props", "chapter", "chapter")
                          ?? throw new InvalidOperationException("Páginas não encontradas.");

        if (!chapterNode.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<Page>();
        var index = 1;

        foreach (var page in pages.EnumerateArray())
        {
            var path = GetString(page, "page_path");
            if (string.IsNullOrWhiteSpace(path) || path.Contains("xml", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new Page
            {
                Number = index++,
                ImageUrl = $"{SourceLocation}/{path.TrimStart('/')}"
            });
        }

        return result;
    }

    private static JsonElement GetPagePayload(IDocument document)
    {
        var data = document.QuerySelector("[data-page]")?.GetAttribute("data-page")
                   ?? throw new InvalidOperationException("data-page não encontrado.");
        return ParseJson(data);
    }

    private string BuildImageUrl(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : $"{SourceLocation}/{path.TrimStart('/')}";

    private static JsonElement? TryGetNestedElement(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current.Clone();
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var node) ? node.GetString() ?? string.Empty : string.Empty;
}
