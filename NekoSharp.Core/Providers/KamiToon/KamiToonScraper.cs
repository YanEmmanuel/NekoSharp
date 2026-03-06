using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.KamiToon;

public sealed class KamiToonScraper : WordPressMadaraScraper
{
    public override string Name => "Kami Toon";

    public KamiToonScraper() : this(null, null) { }

    public KamiToonScraper(LogService? logService) : this(logService, null) { }

    public KamiToonScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://kamitoon.com.br", logService, cfStore)
    { }
}
