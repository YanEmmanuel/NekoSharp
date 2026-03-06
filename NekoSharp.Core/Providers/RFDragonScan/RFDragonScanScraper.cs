using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.RFDragonScan;

public sealed class RFDragonScanScraper : PeachScanScraper
{
    public override string Name => "RF Dragon Scan";

    public RFDragonScanScraper() : this(null, null) { }

    public RFDragonScanScraper(LogService? logService) : this(logService, null) { }

    public RFDragonScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://rfdragonscan.com", logService, cfStore)
    { }
}
