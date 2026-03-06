using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MonteTai;

public sealed class MonteTaiScraper : WordPressMadaraScraper
{
    public override string Name => "Monte Tai";
    protected override string MangaCoverSelector => ".mtx-cover img";
    protected override string MangaDescriptionSelector => ".entry-content.entry-content-single";
    protected override string ChaptersSelector => ".mtx-chapter-item";
    protected override string ChapterLinkSelector => "a";
    protected override string ChapterTitleSelector => ".mtx-chapter-title";

    public MonteTaiScraper() : this(null, null) { }

    public MonteTaiScraper(LogService? logService) : this(logService, null) { }

    public MonteTaiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://montetaiscanlator.xyz", logService, cfStore)
    { }
}
