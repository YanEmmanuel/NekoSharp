using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.XXXYaoi;

public sealed class XXXYaoiScraper : WordPressMadaraScraper
{
    public override string Name => "XXX Yaoi";

    public XXXYaoiScraper() : this(null, null) { }

    public XXXYaoiScraper(LogService? logService) : this(logService, null) { }

    public XXXYaoiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://3xyaoi.com", logService, cfStore)
    { }
}
