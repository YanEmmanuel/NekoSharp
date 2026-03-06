using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.ImperioDaBritannia;

public sealed class ImperioDaBritanniaScraper : WordPressMadaraScraper
{
    public override string Name => "Sagrado Império da Britannia";

    public ImperioDaBritanniaScraper() : this(null, null) { }

    public ImperioDaBritanniaScraper(LogService? logService) : this(logService, null) { }

    public ImperioDaBritanniaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://imperiodabritannia.com", logService, cfStore)
    { }
}
