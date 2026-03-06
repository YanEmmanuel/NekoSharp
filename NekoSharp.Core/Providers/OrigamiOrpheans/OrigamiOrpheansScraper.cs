using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.OrigamiOrpheans;

public sealed class OrigamiOrpheansScraper : MangaThemesiaScraper
{
    public override string Name => "Origami Orpheans";

    public OrigamiOrpheansScraper() : this(null, null) { }

    public OrigamiOrpheansScraper(LogService? logService) : this(logService, null) { }

    public OrigamiOrpheansScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://origami-orpheans.com", logService, cfStore)
    { }
}
