using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.UniversoHentai;

public sealed class UniversoHentaiScraper : GattsuScraper
{
    public override string Name => "Universo Hentai";
    protected override string MangaRootSelector => "div.meio div.post[itemscope]";
    protected override string MangaTitleSelector => "h1.post-titulo";
    protected override string MangaCoverSelector => "img.wp-post-image";
    protected override string MangaDescriptionSelector => "div.post-texto p";
    protected override string ChapterMarkerSelector => "div.meio div.post[itemscope]:has(a[title='Abrir galeria'])";
    protected override string PageSelector => "div.meio div.galeria div.galeria-foto a img";

    public UniversoHentaiScraper() : this(null, null) { }

    public UniversoHentaiScraper(LogService? logService) : this(logService, null) { }

    public UniversoHentaiScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://universohentai.com", logService, cfStore)
    { }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var chapters = await base.GetChaptersAsync(url, ct);
        if (chapters.Count > 0)
            return chapters;

        return
        [
            new Chapter
            {
                Title = "Capítulo único",
                Number = 1,
                Url = url
            }
        ];
    }
}
