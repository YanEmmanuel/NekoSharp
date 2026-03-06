using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.FleurBlanche;

public sealed class FleurBlancheScraper : WordPressMadaraScraper
{
    public override string Name => "Fleur Blanche";

    public FleurBlancheScraper() : this(null, null) { }

    public FleurBlancheScraper(LogService? logService) : this(logService, null) { }

    public FleurBlancheScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://fbsquadx.com", logService, cfStore)
    { }
}
