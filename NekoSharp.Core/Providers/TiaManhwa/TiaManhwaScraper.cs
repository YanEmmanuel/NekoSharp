using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.TiaManhwa;

public sealed class TiaManhwaScraper : WordPressMadaraScraper
{
    public override string Name => "Tia Manhwa";
    protected override string ChaptersSelector => "li.wp-manga-chapter, li.chapter-item, div.chapter";
    protected override string ChapterLinkSelector => "a";
    protected override string ChapterTitleSelector => ".chapternum, a";

    public TiaManhwaScraper() : this(null, null) { }

    public TiaManhwaScraper(LogService? logService) : this(logService, null) { }

    public TiaManhwaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://tiamanhwa.com", logService, cfStore)
    { }
}
