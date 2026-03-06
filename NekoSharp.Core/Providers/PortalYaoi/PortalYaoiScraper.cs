using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.PortalYaoi;

public sealed class PortalYaoiScraper : WordPressMadaraScraper
{
    public override string Name => "Portal Yaoi";

    public PortalYaoiScraper() : this(null, null) { }

    public PortalYaoiScraper(LogService? logService) : this(logService, null) { }

    public PortalYaoiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://lerboyslove.com", logService, cfStore)
    { }
}
