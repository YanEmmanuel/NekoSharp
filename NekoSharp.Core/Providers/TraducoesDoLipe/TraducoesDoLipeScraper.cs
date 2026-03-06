using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.TraducoesDoLipe;

public sealed class TraducoesDoLipeScraper : ZeistMangaScraper
{
    private static readonly Regex ProjectNameRegex = new(@"=\s+?\('([^']+)", RegexOptions.Compiled);
    private static readonly Regex PagesRegex = new(@"=(\[[^]]+])", RegexOptions.Compiled);

    public override string Name => "Traduções do Lipe";
    protected override string MangaCategory => "Projeto";
    protected override string ChapterCategory => "Capítulo";

    public TraducoesDoLipeScraper() : this(null, null) { }

    public TraducoesDoLipeScraper(LogService? logService) : this(logService, null) { }

    public TraducoesDoLipeScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://traducoesdolipe.blogspot.com", logService, cfStore)
    { }

    protected override Manga ParseMangaInfo(IDocument document, string url)
    {
        return new Manga
        {
            Name = document.QuerySelector("meta[property='og:description']")?.GetAttribute("content")?.Trim() ?? string.Empty,
            CoverUrl = document.QuerySelector("meta[property='og:image']")?.GetAttribute("content") ?? string.Empty,
            Description = document.QuerySelector(".synopsis")?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    protected override string GetChapterFeedUrl(IDocument document)
    {
        var projectName = document.QuerySelectorAll("script")
            .Select(node => node.InnerHtml)
            .Select(html => ProjectNameRegex.Match(html))
            .FirstOrDefault(match => match.Success)?
            .Groups[1].Value;

        if (string.IsNullOrWhiteSpace(projectName))
            throw new InvalidOperationException("Nome do projeto não encontrado.");

        return $"{BaseUrl}/feeds/posts/default/-/{Uri.EscapeDataString(ChapterCategory)}/{Uri.EscapeDataString(projectName)}?alt=json&max-results=999";
    }

    protected override List<Page> ParsePages(IDocument document, string chapterUrl)
    {
        var script = document.QuerySelector(".chapter script")?.InnerHtml ?? string.Empty;
        var match = PagesRegex.Match(script);
        if (!match.Success)
            return [];

        using var json = JsonDocument.Parse(match.Groups[1].Value);
        return json.RootElement.EnumerateArray()
            .Select((item, index) => new Page
            {
                Number = index + 1,
                ImageUrl = item.GetString() ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
