using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Exyaoi;

public sealed class ExyaoiScraper : WordPressMadaraScraper
{
    public override string Name => "Exyaoi";
    protected override bool EnableChapterAjax => false;
    protected override string ChaptersSelector => ".page-content-listing.single-page li.wp-manga-chapter > a";

    public ExyaoiScraper() : this(null, null) { }

    public ExyaoiScraper(LogService? logService) : this(logService, null) { }

    public ExyaoiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://3xyaoi.com", logService, cfStore)
    { }
}
