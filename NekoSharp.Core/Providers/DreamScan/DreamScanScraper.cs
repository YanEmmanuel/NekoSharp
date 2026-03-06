using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.DreamScan;

public sealed class DreamScanScraper : WordPressMadaraScraper
{
    public override string Name => "Dream Scan";

    public DreamScanScraper() : this(null, null) { }

    public DreamScanScraper(LogService? logService) : this(logService, null) { }

    public DreamScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://fairydream.com.br", logService, cfStore)
    { }
}
