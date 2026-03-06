using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Risentoons;

public sealed class RisentoonsScraper : StalkerCmsScraper
{
    public override string Name => "Risentoons";

    public RisentoonsScraper() : this(null, null) { }

    public RisentoonsScraper(LogService? logService) : this(logService, null) { }

    public RisentoonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://risentoons.xyz", logService, cfStore)
    { }
}
