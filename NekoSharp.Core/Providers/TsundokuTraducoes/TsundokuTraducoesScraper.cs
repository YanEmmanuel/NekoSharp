using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.TsundokuTraducoes;

public sealed class TsundokuTraducoesScraper : MangaThemesiaScraper
{
    public override string Name => "Tsundoku Traduções";

    public TsundokuTraducoesScraper() : this(null, null) { }

    public TsundokuTraducoesScraper(LogService? logService) : this(logService, null) { }

    public TsundokuTraducoesScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://tsundoku.com.br", logService, cfStore)
    { }
}
