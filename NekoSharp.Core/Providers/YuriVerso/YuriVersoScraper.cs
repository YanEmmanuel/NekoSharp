using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.YuriVerso;

public sealed class YuriVersoScraper : WordPressMadaraScraper
{
    public override string Name => "Yuri Verso";

    public YuriVersoScraper() : this(null, null) { }

    public YuriVersoScraper(LogService? logService) : this(logService, null) { }

    public YuriVersoScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://yuri.live", logService, cfStore)
    { }
}
