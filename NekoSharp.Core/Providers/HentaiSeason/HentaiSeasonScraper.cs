using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.HentaiSeason;

public sealed class HentaiSeasonScraper : GattsuScraper
{
    public override string Name => "Hentai Season";

    public HentaiSeasonScraper() : this(null, null) { }

    public HentaiSeasonScraper(LogService? logService) : this(logService, null) { }

    public HentaiSeasonScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://hentaiseason.com", logService, cfStore)
    { }
}
