using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Vegitoons;

public sealed class VegitoonsScraper : GreenShitScraper
{
    public override string Name => "Vegitoons";
    protected override string ApiUrl => "https://api.vegitoons.black";
    protected override string CdnUrl => "https://cdn.verdinha.wtf";
    protected override string CdnApiUrl => "https://api.vegitoons.black/cdn";
    protected override string ScanId => "1";

    public VegitoonsScraper() : this(null, null) { }

    public VegitoonsScraper(LogService? logService) : this(logService, null) { }

    public VegitoonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://vegitoons.black", logService, cfStore)
    { }
}
