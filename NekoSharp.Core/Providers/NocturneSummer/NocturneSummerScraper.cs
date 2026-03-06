using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.NocturneSummer;

public sealed class NocturneSummerScraper : WordPressMadaraScraper
{
    public override string Name => "Nocturne Summer";

    public NocturneSummerScraper() : this(null, null) { }

    public NocturneSummerScraper(LogService? logService) : this(logService, null) { }

    public NocturneSummerScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://nocfsb.com", logService, cfStore)
    { }
}
