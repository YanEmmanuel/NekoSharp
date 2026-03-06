using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.GhostScan;

public sealed class GhostScanScraper : WordPressMadaraScraper
{
    public override string Name => "Ghost Scan";

    public GhostScanScraper() : this(null, null) { }

    public GhostScanScraper(LogService? logService) : this(logService, null) { }

    public GhostScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://ghostscan.xyz", logService, cfStore)
    { }
}
