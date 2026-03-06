using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.ApenasUmaFa;

public sealed class ApenasUmaFaScraper : ZeistMangaScraper
{
    public override string Name => "Apenas Uma Fã";
    protected override string PageListSelector => "#reader div.separator";

    public ApenasUmaFaScraper() : this(null, null) { }

    public ApenasUmaFaScraper(LogService? logService) : this(logService, null) { }

    public ApenasUmaFaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://apenasuma-fa.blogspot.com", logService, cfStore)
    { }

    protected override Manga ParseMangaInfo(IDocument document, string url)
    {
        var thumbnail = document.QuerySelector("thum")?.GetAttribute("style");
        var coverUrl = Regex.Match(thumbnail ?? string.Empty, "url\\(\"(.*?)\"").Groups[1].Value;

        return new Manga
        {
            Name = document.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ToAbsoluteUrl(url, coverUrl) ?? string.Empty,
            Description = document.QuerySelector("#synopsis")?.TextContent?.Trim() ?? string.Empty,
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
}
