using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MangaStop;

public sealed class MangaStopScraper : MangaThemesiaScraper
{
    public override string Name => "Manga Stop";

    public MangaStopScraper() : this(null, null) { }

    public MangaStopScraper(LogService? logService) : this(logService, null) { }

    public MangaStopScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://mangastop.net", logService, cfStore)
    { }
}
