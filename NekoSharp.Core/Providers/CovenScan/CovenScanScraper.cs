using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.CovenScan;

public sealed class CovenScanScraper : WordPressMadaraScraper
{
    public override string Name => "Coven Scan";

    public CovenScanScraper() : this(null, null) { }

    public CovenScanScraper(LogService? logService) : this(logService, null) { }

    public CovenScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://covendasbruxonas.com", logService, cfStore)
    { }
}
