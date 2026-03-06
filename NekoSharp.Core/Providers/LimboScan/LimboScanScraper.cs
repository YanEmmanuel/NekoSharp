using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.LimboScan;

public sealed class LimboScanScraper : WordPressMadaraScraper
{
    public override string Name => "Limbo Scan";

    public LimboScanScraper() : this(null, null) { }

    public LimboScanScraper(LogService? logService) : this(logService, null) { }

    public LimboScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://limboscan.com.br", logService, cfStore)
    { }
}
