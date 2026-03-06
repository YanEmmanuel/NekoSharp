using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.CafeComYaoi;

public sealed class CafeComYaoiScraper : WordPressMadaraScraper
{
    public override string Name => "Café com Yaoi";

    public CafeComYaoiScraper() : this(null, null) { }

    public CafeComYaoiScraper(LogService? logService) : this(logService, null) { }

    public CafeComYaoiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://cafecomyaoi.com.br", logService, cfStore)
    { }
}
