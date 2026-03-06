using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.KamiSamaExplorer;

public sealed class KamiSamaExplorerScraper : WordPressMadaraScraper
{
    public override string Name => "Kami Sama Explorer";

    public KamiSamaExplorerScraper() : this(null, null) { }

    public KamiSamaExplorerScraper(LogService? logService) : this(logService, null) { }

    public KamiSamaExplorerScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://leitor.kamisama.com.br", logService, cfStore)
    { }
}
