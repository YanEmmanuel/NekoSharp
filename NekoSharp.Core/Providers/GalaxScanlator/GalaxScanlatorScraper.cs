using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.GalaxScanlator;

public sealed class GalaxScanlatorScraper : ZeistMangaScraper
{
    public override string Name => "GALAX Scans";
    protected override string PageListSelector => "#reader";
    protected override bool UseNewChapterFeed => true;
    protected override string ChapterCategory => "Capítulo";

    public GalaxScanlatorScraper() : this(null, null) { }

    public GalaxScanlatorScraper(LogService? logService) : this(logService, null) { }

    public GalaxScanlatorScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://galaxscanlator.blogspot.com", logService, cfStore)
    { }
}
