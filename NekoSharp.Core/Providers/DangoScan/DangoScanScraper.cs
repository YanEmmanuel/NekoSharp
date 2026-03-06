using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.DangoScan;

public sealed class DangoScanScraper : PeachScanScraper
{
    public override string Name => "Dango Scan";

    public DangoScanScraper() : this(null, null) { }

    public DangoScanScraper(LogService? logService) : this(logService, null) { }

    public DangoScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://dangoscan.com.br", logService, cfStore)
    { }
}
