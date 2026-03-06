using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.PinkSeaUnicorn;

public sealed class PinkSeaUnicornScraper : WordPressMadaraScraper
{
    public override string Name => "Pink Sea Unicorn";

    public PinkSeaUnicornScraper() : this(null, null) { }

    public PinkSeaUnicornScraper(LogService? logService) : this(logService, null) { }

    public PinkSeaUnicornScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://psunicorn.com", logService, cfStore)
    { }
}
