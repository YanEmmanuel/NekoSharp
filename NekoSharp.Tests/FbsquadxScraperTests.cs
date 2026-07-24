using System.Net;
using NekoSharp.Core.Providers.fbsquadx;
using Xunit;

namespace NekoSharp.Tests;

public sealed class FbsquadxScraperTests
{
    [Fact]
    public async Task GetChaptersAsync_LoadsMadaraNewChapterEndpoint()
    {
        var requests = new List<(HttpMethod Method, string Url, bool IsAjax)>();
        var handler = new StubHandler(request =>
        {
            requests.Add((
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Contains("X-Requested-With")));

            var html = request.Method == HttpMethod.Get
                ? "<div id='manga-chapters-holder-1' data-id='42'></div>"
                : "<li class='wp-manga-chapter'><a href='/manga/teste/capitulo-12/'>Capítulo 12 - O começo</a></li>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
                RequestMessage = request
            };
        });
        var scraper = new fbsquadxScraper(handler);

        var chapters = await scraper.GetChaptersAsync("https://fbsquadx.com/manga/teste/");

        Assert.Equal(
            [
                (HttpMethod.Get, "https://fbsquadx.com/manga/teste/", false),
                (HttpMethod.Post, "https://fbsquadx.com/manga/teste/ajax/chapters", true)
            ],
            requests);
        var chapter = Assert.Single(chapters);
        Assert.Equal(12, chapter.Number);
        Assert.Equal("O começo", chapter.Title);
        Assert.Equal("https://fbsquadx.com/manga/teste/capitulo-12/?style=list", chapter.Url);
    }

    [Fact]
    public async Task GetMangaInfoAsync_WhenRedirectedToLogin_ExplainsAuthenticationRequirement()
    {
        var scraper = new fbsquadxScraper(new StubHandler(request =>
        {
            request.RequestUri = new Uri("https://fbsquadx.com/wp-login.php?redirect_to=%2Fmanga%2Fteste");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<form id='loginform'></form>"),
                RequestMessage = request
            };
        }));

        var error = await Assert.ThrowsAsync<IOException>(
            () => scraper.GetMangaInfoAsync("https://fbsquadx.com/manga/teste/"));

        Assert.Contains("login via WebView", error.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
