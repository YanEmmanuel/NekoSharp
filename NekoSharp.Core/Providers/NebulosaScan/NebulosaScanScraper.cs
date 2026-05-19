using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.NebulosaScan;

public sealed class NebulosaScanScraper : WordPressMadaraScraper
{
    public override string Name => "Nebulosa Scan";

    public NebulosaScanScraper() : this(null, null) { }

    public NebulosaScanScraper(LogService? logService) : this(logService, null) { }

    public NebulosaScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://nebulosascan.com", logService, cfStore)
    { }
}
