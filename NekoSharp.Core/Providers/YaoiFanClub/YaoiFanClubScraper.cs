using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.YaoiFanClub;

public sealed class YaoiFanClubScraper : ZeistMangaScraper
{
    public override string Name => "Yaoi Fan Club";
    protected override bool UseNewChapterFeed => true;
    protected override string ChapterCategory => "Chapter";

    public YaoiFanClubScraper() : this(null, null) { }

    public YaoiFanClubScraper(LogService? logService) : this(logService, null) { }

    public YaoiFanClubScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://www.yaoifanclub.com", logService, cfStore)
    { }
}
