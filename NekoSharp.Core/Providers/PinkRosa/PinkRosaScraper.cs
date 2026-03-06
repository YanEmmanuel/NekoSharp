using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.PinkRosa;

public sealed class PinkRosaScraper : ZeistMangaScraper
{
    private static readonly Regex ThumbnailRegex = new(@"url.""([^""]+)""", RegexOptions.Compiled);

    public override string Name => "Pink Rosa";
    protected override string PageListSelector => "div.separator a";

    public PinkRosaScraper() : this(null, null) { }

    public PinkRosaScraper(LogService? logService) : this(logService, null) { }

    public PinkRosaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://scanpinkrosa.blogspot.com", logService, cfStore)
    { }

    protected override Manga ParseMangaInfo(IDocument document, string url)
    {
        var style = document.QuerySelector(".thum")?.GetAttribute("style") ?? string.Empty;
        var cover = ThumbnailRegex.Match(style).Groups[1].Value;

        return new Manga
        {
            Name = document.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ToAbsoluteUrl(url, cover) ?? string.Empty,
            Description = document.QuerySelector("#syn_bod")?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    protected override string GetChapterFeedUrl(IDocument document)
    {
        var label = document.QuerySelector(".chapter_get")?.GetAttribute("data-labelchapter")
                    ?? throw new InvalidOperationException("Feed de capítulos não encontrado.");
        return $"{BaseUrl}/feeds/posts/default/-/{Uri.EscapeDataString(label)}?alt=json&start-index=1&max-results=999";
    }

    protected override List<Page> ParsePages(IDocument document, string chapterUrl)
    {
        return document.QuerySelectorAll(PageListSelector)
            .Select((element, index) => new Page
            {
                Number = index + 1,
                ImageUrl = ToAbsoluteUrl(chapterUrl, element.GetAttribute("href")) ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
