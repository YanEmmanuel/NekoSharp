using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MangaLivreTo;

public sealed class MangaLivreToScraper : WordPressMadaraScraper
{
    public override string Name => "Manga Livre.to";
    protected override string ChaptersSelector => ".listing-chapters-wrap .chapter-box";
    protected override string ChapterLinkSelector => "a";
    protected override string ChapterTitleSelector => "a";

    public MangaLivreToScraper() : this(null, null) { }

    public MangaLivreToScraper(LogService? logService) : this(logService, null) { }

    public MangaLivreToScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://mangalivre.to", logService, cfStore)
    { }
}
