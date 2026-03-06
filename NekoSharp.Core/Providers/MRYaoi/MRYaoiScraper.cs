using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MRYaoi;

public sealed class MRYaoiScraper : WordPressMadaraScraper
{
    public override string Name => "MR Yaoi";

    public MRYaoiScraper() : this(null, null) { }

    public MRYaoiScraper(LogService? logService) : this(logService, null) { }

    public MRYaoiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://mrtenzus.com", logService, cfStore)
    { }
}
