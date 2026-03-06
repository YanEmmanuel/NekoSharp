using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.KairosToons;

public sealed class KairosToonsScraper : StalkerCmsScraper
{
    public override string Name => "Kairos Toons";

    public KairosToonsScraper() : this(null, null) { }

    public KairosToonsScraper(LogService? logService) : this(logService, null) { }

    public KairosToonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://kairostoons.net", logService, cfStore)
    { }
}
