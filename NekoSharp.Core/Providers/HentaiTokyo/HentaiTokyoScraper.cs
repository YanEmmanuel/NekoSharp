using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.HentaiTokyo;

public sealed class HentaiTokyoScraper : GattsuScraper
{
    public override string Name => "Hentai Tokyo";

    public HentaiTokyoScraper() : this(null, null) { }

    public HentaiTokyoScraper(LogService? logService) : this(logService, null) { }

    public HentaiTokyoScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://hentaitokyo.net", logService, cfStore)
    { }
}
