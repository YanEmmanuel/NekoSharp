using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MugiwarasOficial;

public sealed class MugiwarasOficialScraper : WordPressMadaraScraper
{
    public override string Name => "Mugiwaras Oficial";

    public MugiwarasOficialScraper() : this(null, null) { }

    public MugiwarasOficialScraper(LogService? logService) : this(logService, null) { }

    public MugiwarasOficialScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://mugiwarasoficial.com", logService, cfStore)
    { }
}
