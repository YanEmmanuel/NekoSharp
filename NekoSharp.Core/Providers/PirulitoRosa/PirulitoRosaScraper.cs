using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.PirulitoRosa;

public sealed class PirulitoRosaScraper : WordPressMadaraScraper
{
    public override string Name => "Pirulito Rosa";

    public PirulitoRosaScraper() : this(null, null) { }

    public PirulitoRosaScraper(LogService? logService) : this(logService, null) { }

    public PirulitoRosaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://pirulitorosa.site", logService, cfStore)
    { }
}
