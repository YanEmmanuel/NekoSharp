using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.HotCabaretScan;

public sealed class HotCabaretScanScraper : WordPressMadaraScraper
{
    public override string Name => "Hot Cabaret Scan";

    public HotCabaretScanScraper() : this(null, null) { }

    public HotCabaretScanScraper(LogService? logService) : this(logService, null) { }

    public HotCabaretScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://hotcabaretscan.com", logService, cfStore)
    { }
}
