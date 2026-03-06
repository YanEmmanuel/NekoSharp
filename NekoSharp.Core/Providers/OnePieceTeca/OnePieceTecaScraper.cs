using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.OnePieceTeca;

public sealed class OnePieceTecaScraper : WordPressMadaraScraper
{
    public override string Name => "One Piece TECA";

    public OnePieceTecaScraper() : this(null, null) { }

    public OnePieceTecaScraper(LogService? logService) : this(logService, null) { }

    public OnePieceTecaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://onepieceteca.com", logService, cfStore)
    { }
}
