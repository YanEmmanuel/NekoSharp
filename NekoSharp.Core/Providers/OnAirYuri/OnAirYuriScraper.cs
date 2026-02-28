using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.OnAirYuri;

public sealed class OnAirYuriScraper : WordPressMadaraScraper
{
    public override string Name => "OnAirYuri";

    public OnAirYuriScraper() : this(null, null) { }

    public OnAirYuriScraper(LogService? logService) : this(logService, null) { }

    public OnAirYuriScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://yurionair.top", logService, cfStore)
    { }
}
