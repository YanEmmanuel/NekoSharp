using System.Net;
using NekoSharp.Core.Providers.Comix;
using Xunit;

namespace NekoSharp.Tests;

public class ComixScraperTests
{
    [Fact]
    public void ParseChapterPayload_MapsCurrentSitePayload()
    {
        const string payload =
            """
            [
              {
                "id": 5001,
                "mangaId": 123,
                "url": "/title/45z4-usemono/5001-chapter-76",
                "number": 76,
                "name": "United Front",
                "votes": 10,
                "isOfficial": true,
                "group": null
              },
              {
                "id": 5002,
                "mangaId": 123,
                "number": 75.5,
                "name": "",
                "votes": 20,
                "isOfficial": false,
                "group": { "id": 307, "name": "Violet Scans" }
              }
            ]
            """;

        var candidates = ComixScraper.ParseChapterPayload(payload);

        Assert.Collection(
            candidates,
            chapter =>
            {
                Assert.Equal(5001, chapter.ChapterId);
                Assert.Equal(76, chapter.Number);
                Assert.Equal(1, chapter.IsOfficial);
                Assert.Equal("/title/45z4-usemono/5001-chapter-76", chapter.SourceUrl);
            },
            chapter =>
            {
                Assert.Equal(5002, chapter.ChapterId);
                Assert.Equal(75.5, chapter.Number);
                Assert.Equal(307, chapter.ScanlationGroupId);
                Assert.Equal("Violet Scans", chapter.ScanlationGroupName);
            });
    }

    [Fact]
    public void BuildChapterList_MirrorsExtensionChapterNamesAndKeepsDuplicates()
    {
        var chapters = ComixScraper.BuildChapterList(
            "45z4-usemono-ari-no-houichi",
            [
                new ComixScraper.ComixChapterCandidate(
                    ChapterId: 5001,
                    Number: 76,
                    SourceUrl: "/title/45z4-usemono-ari-no-houichi/5001-chapter-76",
                    Name: "United Front",
                    Votes: 10,
                    UpdatedAt: 5001,
                    ScanlationGroupId: 0,
                    ScanlationGroupName: string.Empty,
                    IsOfficial: 1),
                new ComixScraper.ComixChapterCandidate(
                    ChapterId: 5002,
                    Number: 76,
                    SourceUrl: string.Empty,
                    Name: "United Front",
                    Votes: 20,
                    UpdatedAt: 5002,
                    ScanlationGroupId: 307,
                    ScanlationGroupName: "Violet Scans",
                    IsOfficial: 0)
            ]);

        Assert.Collection(
            chapters,
            chapter =>
            {
                Assert.Equal("Chapter 76: United Front", chapter.Title);
                Assert.Equal(
                    "https://comix.to/title/45z4-usemono-ari-no-houichi/5001-chapter-76",
                    chapter.Url);
            },
            chapter =>
            {
                Assert.Equal("Chapter 76: United Front", chapter.Title);
                Assert.Equal(
                    "https://comix.to/title/45z4-usemono-ari-no-houichi/5002-chapter-76",
                    chapter.Url);
            });
    }

    [Fact]
    public void ParsePagePayload_MarksFlaggedAndEveryFourthPageAsScrambled()
    {
        const string payload =
            """
            {
              "result": {
                "pages": {
                  "baseUrl": "https://wowpic.example/si/chapter",
                  "items": [
                    { "url": "001.jpg", "s": 0 },
                    { "url": "002.jpg", "s": 1 },
                    { "url": "https://cdn.example/003.jpg", "s": 0 },
                    { "url": "004.jpg", "s": 0 }
                  ]
                }
              }
            }
            """;

        var pages = ComixScraper.ParsePagePayload(
            payload,
            "https://comix.to/title/abc/123-chapter");

        Assert.Equal(4, pages.Count);
        Assert.Equal("https://wowpic.example/si/chapter/001.jpg", pages[0].ImageUrl);
        Assert.Equal("https://wowpic.example/si/chapter/002.jpg#scrambled", pages[1].ImageUrl);
        Assert.Equal("https://cdn.example/003.jpg", pages[2].ImageUrl);
        Assert.Equal("https://wowpic.example/si/chapter/004.jpg#scrambled", pages[3].ImageUrl);
        Assert.All(pages, page =>
            Assert.Equal("https://comix.to/title/abc/123-chapter", page.RefererUrl));
    }

    [Fact]
    public void GetPageDownloadCandidates_ProvidesAllScramblePathFallbacks()
    {
        var scraper = new ComixScraper();

        var candidates = scraper.GetPageDownloadCandidates(
            "https://wowpic.example/si/chapter/001.jpg#scrambled");

        Assert.Equal(
            [
                "https://wowpic.example/si/chapter/001.jpg#scrambled",
                "https://wowpic.example/i/chapter/001.jpg#scrambled",
                "https://wowpic.example/sii/chapter/001.jpg#scrambled",
                "https://wowpic.example/ii/chapter/001.jpg#scrambled"
            ],
            candidates);
    }

    [Fact]
    public void ApplyPageDownloadHeaders_RemovesOriginForExternalUnscrambledImages()
    {
        var scraper = new ComixScraper();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://wowpic.example/si/chapter/001.jpg");
        request.Headers.TryAddWithoutValidation("Origin", "https://comix.to");

        scraper.ApplyPageDownloadHeaders(request, request.RequestUri!.ToString());

        Assert.False(request.Headers.Contains("Origin"));
        Assert.Equal("https://comix.to/", request.Headers.Referrer?.ToString());
        Assert.Equal("*/*", request.Headers.GetValues("Accept").Single());
    }

    [Fact]
    public void ApplyPageDownloadHeaders_KeepsOriginAndStripsMarkerForScrambledImages()
    {
        var scraper = new ComixScraper();
        const string imageUrl = "https://wowpic.example/si/chapter/004.jpg#scrambled";
        using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);

        scraper.ApplyPageDownloadHeaders(request, imageUrl);

        Assert.Equal("https://wowpic.example/si/chapter/004.jpg", request.RequestUri?.ToString());
        Assert.Equal("https://comix.to", request.Headers.GetValues("Origin").Single());
    }

    [Fact]
    public async Task CopyPageResponseAsync_DecodesEncodedPrefixFromResponseHeaders()
    {
        var scraper = new ComixScraper();
        var original = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var encoded = ComixScraper.DecodeEncodedPrefix(original, seed: 12345, encodedLength: 40);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encoded)
        };
        response.Headers.TryAddWithoutValidation("x-enc-seed", "12345");
        response.Headers.TryAddWithoutValidation("x-enc-len", "40");
        await using var output = new MemoryStream();

        await scraper.CopyPageResponseAsync(
            response,
            output,
            "https://wowpic.example/si/chapter/004.jpg#scrambled");

        Assert.Equal(original, output.ToArray());
    }

    [Theory]
    [InlineData("https://wowpic.example/i/chapter/004.webp#scrambled", true)]
    [InlineData("https://wowpic.example/i/chapter/004.webp", false)]
    public void ShouldUseRenderedPageFallback_OnlyHandlesScrambledPages(
        string imageUrl,
        bool expected)
    {
        var scraper = new ComixScraper();

        Assert.Equal(expected, scraper.ShouldUseRenderedPageFallback(imageUrl));
    }

    [Theory]
    [InlineData("https://cdn.example/page.webp#scrambled", "image/webp")]
    [InlineData("https://cdn.example/page.jpg#scrambled", "image/jpeg")]
    [InlineData("https://cdn.example/page.jpeg#scrambled", "image/jpeg")]
    [InlineData("https://cdn.example/page.png#scrambled", "image/png")]
    [InlineData("not-a-url", "image/png")]
    public void GetCanvasMimeType_UsesImageExtension(string imageUrl, string expected)
    {
        Assert.Equal(expected, ComixScraper.GetCanvasMimeType(imageUrl));
    }

    [Fact]
    public void DecodeCanvasDataUrl_DecodesBase64Payload()
    {
        var expected = new byte[] { 1, 2, 3, 4 };

        var decoded = ComixScraper.DecodeCanvasDataUrl(
            $"data:image/png;base64,{Convert.ToBase64String(expected)}");

        Assert.Equal(expected, decoded);
    }
}
