using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.FenixProject;

public sealed class FenixProjectScraper : WordPressMadaraScraper
{
    public override string Name => "Fenix Project";

    public FenixProjectScraper() : this(null, null) { }

    public FenixProjectScraper(LogService? logService) : this(logService, null) { }

    public FenixProjectScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://fenixproject.site", logService, cfStore)
    { }
}
