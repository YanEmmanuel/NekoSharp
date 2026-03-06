using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.TemakiMangas;

public sealed class TemakiMangasScraper : ZeistMangaScraper
{
    public override string Name => "Temaki mangás";
    protected override string PageListSelector => "#reader div.separator";

    public TemakiMangasScraper() : this(null, null) { }

    public TemakiMangasScraper(LogService? logService) : this(logService, null) { }

    public TemakiMangasScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://temakimangas.blogspot.com", logService, cfStore)
    { }

    protected override Manga ParseMangaInfo(IDocument document, string url)
    {
        var header = document.QuerySelector("header") ?? GetDocumentRoot(document);
        return new Manga
        {
            Name = header.QuerySelector("h1")?.TextContent?.Trim() ?? string.Empty,
            CoverUrl = ExtractImageSource(header.QuerySelector(".thumb"), url) ?? string.Empty,
            Description = document.QuerySelector("#synopsis")?.TextContent?.Trim() ?? string.Empty,
            Url = url,
            SiteName = Name
        };
    }
}
