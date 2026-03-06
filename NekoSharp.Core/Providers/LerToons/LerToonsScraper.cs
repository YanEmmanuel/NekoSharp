using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.LerToons;

public sealed class LerToonsScraper : ZeroThemeScraper
{
    public override string Name => "Ler Toons";
    protected override string CdnUrl => "https://fullmangas.one";
    protected override string ImageLocation => string.Empty;

    public LerToonsScraper() : this(null, null) { }

    public LerToonsScraper(LogService? logService) : this(logService, null) { }

    public LerToonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://www.readmangas.org", logService, cfStore)
    { }
}
