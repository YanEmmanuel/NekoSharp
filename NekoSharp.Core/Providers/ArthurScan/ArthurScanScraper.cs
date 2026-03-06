using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.ArthurScan;

public sealed class ArthurScanScraper : WordPressMadaraScraper
{
    public override string Name => "Arthur Scan";

    public ArthurScanScraper() : this(null, null) { }

    public ArthurScanScraper(LogService? logService) : this(logService, null) { }

    public ArthurScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://arthurscan.xyz", logService, cfStore)
    { }
}
