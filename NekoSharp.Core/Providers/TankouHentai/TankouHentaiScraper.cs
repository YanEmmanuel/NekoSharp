using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.TankouHentai;

public sealed class TankouHentaiScraper : WordPressMadaraScraper
{
    public override string Name => "Tankou Hentai";

    public TankouHentaiScraper() : this(null, null) { }

    public TankouHentaiScraper(LogService? logService) : this(logService, null) { }

    public TankouHentaiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://tankouhentai.com", logService, cfStore)
    { }
}
