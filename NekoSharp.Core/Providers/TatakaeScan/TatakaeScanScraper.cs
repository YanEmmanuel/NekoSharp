using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.TatakaeScan;

public sealed class TatakaeScanScraper : WordPressMadaraScraper
{
    public override string Name => "Tatakae Scan";

    public TatakaeScanScraper() : this(null, null) { }

    public TatakaeScanScraper(LogService? logService) : this(logService, null) { }

    public TatakaeScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://tatakaescan.com", logService, cfStore)
    { }
}
