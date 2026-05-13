using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.FlowerMangaDotNet;

public sealed class FlowerMangaDotNetScraper : WordPressMadaraScraper
{
    public override string Name => "FlowerManga.net";
    protected override bool UseNewChapterEndpoint => false;

    public FlowerMangaDotNetScraper() : this(null, null) { }

    public FlowerMangaDotNetScraper(LogService? logService) : this(logService, null) { }

    public FlowerMangaDotNetScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://flowermanga.org", logService, cfStore)
    { }
}
