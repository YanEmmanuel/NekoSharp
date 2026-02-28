using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.CovenDasBruxonas;

public sealed class CovenDasBruxonasScraper : WordPressMadaraScraper
{
    public override string Name => "CovenDasBruxonas";

    public CovenDasBruxonasScraper() : this(null, null) { }

    public CovenDasBruxonasScraper(LogService? logService) : this(logService, null) { }

    public CovenDasBruxonasScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://covendasbruxonas.com", logService, cfStore)
    { }
}
