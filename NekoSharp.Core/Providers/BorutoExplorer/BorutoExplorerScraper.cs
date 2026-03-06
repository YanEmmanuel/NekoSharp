using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.BorutoExplorer;

public sealed class BorutoExplorerScraper : WordPressMadaraScraper
{
    public override string Name => "Boruto Explorer";

    public BorutoExplorerScraper() : this(null, null) { }

    public BorutoExplorerScraper(LogService? logService) : this(logService, null) { }

    public BorutoExplorerScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://leitor.borutoexplorer.com.br", logService, cfStore)
    { }
}
