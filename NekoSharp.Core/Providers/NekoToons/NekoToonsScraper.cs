using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.NekoToons;

public sealed class NekoToonsScraper : YuYuScraper
{
    public override string Name => "Neko Toons";

    public NekoToonsScraper() : this(null, null) { }

    public NekoToonsScraper(LogService? logService) : this(logService, null) { }

    public NekoToonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://nekotoons.site", logService, cfStore)
    { }
}
