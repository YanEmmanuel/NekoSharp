using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Verdinha;

public sealed class VerdinhaScraper : GreenShitScraper
{
    public override string Name => "Verdinha";
    protected override string ApiUrl => "https://api.verdinha.wtf";
    protected override string CdnUrl => "https://cdn.verdinha.wtf";
    protected override string CdnApiUrl => "https://api.verdinha.wtf/cdn";
    protected override string ScanId => "1";

    public VerdinhaScraper() : this(null, null) { }

    public VerdinhaScraper(LogService? logService) : this(logService, null) { }

    public VerdinhaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://verdinha.wtf", logService, cfStore)
    { }
}
