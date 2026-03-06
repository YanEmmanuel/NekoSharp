using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Templates;

public abstract class YuYuScraper : HtmlScraperBase
{
    protected virtual string MangaRootSelector => ".manga-banner .container";
    protected virtual string MangaTitleSelector => "h1";
    protected virtual string MangaCoverSelector => "img";
    protected virtual string MangaDescriptionSelector => ".sinopse p";
    protected virtual string ChapterSelector => "a.chapter-item";
    protected virtual string ChapterTitleSelector => ".capitulo-numero";
    protected virtual string PageSelector => "picture img";

    private static readonly Regex MangaIdRegex = new(@"obra_id:\s+(\d+)", RegexOptions.Compiled);

    protected YuYuScraper(string baseUrl, LogService? logService, CloudflareCredentialStore? cfStore)
        : base(baseUrl, logService, cfStore)
    {
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var root = document.QuerySelector(MangaRootSelector) ?? GetDocumentRoot(document);

        return new Manga
        {
            Name = root.QuerySelector(MangaTitleSelector)?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(root.QuerySelector(MangaCoverSelector), url) ?? string.Empty,
            Description = root.QuerySelector(MangaDescriptionSelector)?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var document = await LoadDocumentAsync(url, ct);
        var mangaId = ResolveMangaId(document);
        var chapters = new List<Chapter>();
        var page = 1;

        do
        {
            var payload = await GetJsonAsync($"{BaseUrl}/ajax/lzmvke.php?order=DESC&manga_id={mangaId}&page={page}", ct);
            if (!payload.TryGetProperty("chapters", out var htmlNode))
                break;

            var html = htmlNode.GetString();
            if (string.IsNullOrWhiteSpace(html))
                break;

            var fragment = await OpenDocumentAsync($"<body>{html}</body>", url, ct);
            chapters.AddRange(
                fragment.QuerySelectorAll(ChapterSelector)
                    .Select(element => MapChapter(element, url))
                    .Where(chapter => chapter is not null)
                    .Cast<Chapter>());

            page++;

            if (!payload.TryGetProperty("remaining", out var remainingNode) || remainingNode.GetInt32() <= 0)
                break;
        } while (true);

        return chapters;
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var document = await LoadDocumentAsync(chapter.Url, ct);
        return CreatePagesFromNodes(document.QuerySelectorAll(PageSelector), chapter.Url);
    }

    protected virtual Chapter? MapChapter(IElement element, string refererUrl)
    {
        var title = element.QuerySelector(ChapterTitleSelector)?.TextContent?.Trim()
                    ?? element.TextContent?.Trim()
                    ?? string.Empty;
        var url = ToAbsoluteUrl(refererUrl, element.GetAttribute("href"));

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            return null;

        return new Chapter
        {
            Title = title,
            Url = url,
            Number = ChapterHelper.ExtractChapterNumber(title)
        };
    }

    protected virtual string ResolveMangaId(IDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script"))
        {
            var match = MangaIdRegex.Match(script.TextContent ?? string.Empty);
            if (match.Success)
                return match.Groups[1].Value;
        }

        throw new InvalidOperationException("Manga ID não encontrado.");
    }
}
