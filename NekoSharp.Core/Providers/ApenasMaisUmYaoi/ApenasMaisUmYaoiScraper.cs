using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.ApenasMaisUmYaoi;

public sealed class ApenasMaisUmYaoiScraper : WordPressMadaraScraper
{
    public override string Name => "ApenasMaisUmYaoi";

    protected override string PagesSelector => "div.page-break";

    public ApenasMaisUmYaoiScraper() : this(null, null) { }

    public ApenasMaisUmYaoiScraper(LogService? logService) : this(logService, null) { }

    public ApenasMaisUmYaoiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://apenasmaisumyaoi.com", logService, cfStore)
    { }
}
