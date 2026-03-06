using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.StarlightScan;

public sealed class StarlightScanScraper : MangaThemesiaScraper
{
    public override string Name => "Starlight Scan";
    protected override string SeriesDetailsSelector => "section.mangaDetails";
    protected override string SeriesTitleSelector => "h1.mangaDetails__title";
    protected override string SeriesDescriptionSelector => "span.mangaDetails__description";
    protected override string SeriesThumbnailSelector => "img.mangaDetails__cover";
    protected override string ChapterListSelector => "div.mangaDetails__episodesContainer div.mangaDetails__episode";
    protected override string ChapterUrlSelector => "a";
    protected override string ChapterNameSelector => "a.mangaDetails__episodeTitle";
    protected override string PageSelector => "div.scanImagesContainer img.scanImage";

    public StarlightScanScraper() : this(null, null) { }

    public StarlightScanScraper(LogService? logService) : this(logService, null) { }

    public StarlightScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://starligthscan.com", logService, cfStore)
    { }
}
