using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.OsakaScan;

public sealed class OsakaScanScraper : ZeistMangaScraper
{
    public override string Name => "Osaka Scan";
    protected override string PageListSelector => "#reader div.separator";

    public OsakaScanScraper() : this(null, null) { }

    public OsakaScanScraper(LogService? logService) : this(logService, null) { }

    public OsakaScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://www.osakascan.com", logService, cfStore)
    { }

    protected override Manga ParseMangaInfo(IDocument document, string url)
    {
        return new Manga
        {
            Name = document.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = string.Empty,
            Description = document.QuerySelector("#synopsis")?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }

    protected override IEnumerable<Chapter> OrderChapters(IEnumerable<Chapter> chapters)
        => chapters.OrderByDescending(chapter => chapter.Number);
}
