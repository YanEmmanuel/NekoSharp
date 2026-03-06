using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MiniTwoScan;

public sealed class MiniTwoScanScraper : WordPressMadaraScraper
{
    public override string Name => "MiniTwo Scan";

    public MiniTwoScanScraper() : this(null, null) { }

    public MiniTwoScanScraper(LogService? logService) : this(logService, null) { }

    public MiniTwoScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://minitwoscan.com", logService, cfStore)
    { }
}
