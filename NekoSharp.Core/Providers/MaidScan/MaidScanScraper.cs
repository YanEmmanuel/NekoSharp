using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MaidScan;

public sealed class MaidScanScraper : GreenShitScraper
{
    public override string Name => "Maid Scan";
    protected override string ApiUrl => "https://api.verdinha.wtf";
    protected override string CdnUrl => "https://cdn.verdinha.wtf";
    protected override string CdnApiUrl => "https://api.verdinha.wtf/cdn";
    protected override string ScanId => "3";

    public MaidScanScraper() : this(null, null) { }

    public MaidScanScraper(LogService? logService) : this(logService, null) { }

    public MaidScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://empreguetes.xyz", logService, cfStore)
    { }
}
