using System.Net;
using NekoSharp.Core.Providers.FlowerMangaDotNet;
using Xunit;

namespace NekoSharp.Tests;

public sealed class FlowerMangaDotNetScraperTests
{
    [Fact]
    public async Task GetsChaptersAndPagesFromCurrentFlowerMangaMarkup()
    {
        var scraper = new FlowerMangaDotNetScraper(new StubHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("capitulo-77")
                ? "<div class='page-break'><img class='wp-manga-chapter-img' data-src='/images/001.jpg'></div>"
                : "<script id='mk-chapters-data' type='application/json'>{\"items\":[{\"num\":\"77\",\"name\":\"Capítulo 77\",\"url\":\"/manga/teste/capitulo-77/\"}]}</script>"));

        var chapter = Assert.Single(await scraper.GetChaptersAsync("https://flowermanga.org/manga/teste/"));
        var page = Assert.Single(await scraper.GetPagesAsync(chapter));

        Assert.Equal(77, chapter.Number);
        Assert.Equal("https://flowermanga.org/manga/teste/capitulo-77/", chapter.Url);
        Assert.Equal("https://flowermanga.org/images/001.jpg", page.ImageUrl);
        Assert.Equal(chapter.Url, page.RefererUrl);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content(request)) });
    }
}
