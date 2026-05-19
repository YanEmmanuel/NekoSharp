using NekoSharp.Core.Providers.Comix;
using Xunit;

namespace NekoSharp.Tests;

public class ComixScraperTests
{
    [Fact]
    public void GenerateHash_KnownChapterPath_MatchesExtensionSource()
    {
        var token = ComixHash.GenerateHash("/manga/5vwvl/chapters", 0, 1);

        Assert.Equal("xQm9tJfLwGhz_0Eq8S_YAHYkwp-q1PLfm50W5QJnyd1NnNYpAjXjyCoAzoOLrrymJN0xWS0NeDGz_rNrbqBjLLP1H9qi", token);
    }

    [Fact]
    public void BuildChapterListRelativeUrl_AddsSignedParametersRequiredByApi()
    {
        var relativeUrl = ComixScraper.BuildChapterListRelativeUrl("5vwvl", 2, 100, "token-123");

        Assert.Equal("manga/5vwvl/chapters?order%5Bnumber%5D=desc&limit=100&page=2&_=token-123", relativeUrl);
    }

    [Fact]
    public void HttpClient_ResolvesFullUrlWithHashParams()
    {
        using var http = new System.Net.Http.HttpClient
        {
            BaseAddress = new System.Uri("https://comix.to/api/v1/")
        };
        var relativeUrl = ComixScraper.BuildChapterListRelativeUrl("r9qjm", 1, 100, "token-abc");
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, relativeUrl);
        var resolvedUri = new System.Uri(http.BaseAddress!, request.RequestUri!);
        var absString = resolvedUri.AbsoluteUri;

        Assert.Contains("&_=", absString);
        Assert.DoesNotContain("%255B", absString);
        Assert.DoesNotContain("%255D", absString);
    }

    [Fact]
    public void BuildPageListRelativeUrl_AddsSignedParametersRequiredByApi()
    {
        var relativeUrl = ComixScraper.BuildPageListRelativeUrl(5241183, "page-token");

        Assert.Equal("chapters/5241183?_=page-token", relativeUrl);
    }

    [Fact]
    public void BuildChapterList_OfficialAndUnofficialSameChapter_KeepsBothWithDifferentTitles()
    {
        var chapters = ComixScraper.BuildChapterList(
            "45z4-usemono-ari-no-houichi",
            [
                new ComixScraper.ComixChapterCandidate(
                    ChapterId: 5001,
                    Number: 76,
                    SourceUrl: string.Empty,
                    Name: "United Front",
                    Votes: 10,
                    UpdatedAt: 200,
                    ScanlationGroupId: 10702,
                    ScanlationGroupName: string.Empty,
                    IsOfficial: 1),
                new ComixScraper.ComixChapterCandidate(
                    ChapterId: 5002,
                    Number: 76,
                    SourceUrl: string.Empty,
                    Name: "United Front",
                    Votes: 20,
                    UpdatedAt: 150,
                    ScanlationGroupId: 1234,
                    ScanlationGroupName: "Fan Scans",
                    IsOfficial: 0)
            ]);

        Assert.Collection(
            chapters,
            chapter =>
            {
                Assert.Equal(76, chapter.Number);
                Assert.Equal("United Front [Oficial]", chapter.Title);
                Assert.Equal("https://comix.to/title/45z4-usemono-ari-no-houichi/5001", chapter.Url);
            },
            chapter =>
            {
                Assert.Equal(76, chapter.Number);
                Assert.Equal("United Front [Fan Scans]", chapter.Title);
                Assert.Equal("https://comix.to/title/45z4-usemono-ari-no-houichi/5002", chapter.Url);
            });
    }

    [Fact]
    public void BuildChapterList_SingleChapter_KeepsOriginalTitleWithoutVariantSuffix()
    {
        var chapters = ComixScraper.BuildChapterList(
            "45z4-usemono-ari-no-houichi",
            [
                new ComixScraper.ComixChapterCandidate(
                    ChapterId: 5003,
                    Number: 77,
                    SourceUrl: string.Empty,
                    Name: "The Battle",
                    Votes: 5,
                    UpdatedAt: 300,
                    ScanlationGroupId: 1234,
                    ScanlationGroupName: "Fan Scans",
                    IsOfficial: 0)
            ]);

        var chapter = Assert.Single(chapters);
        Assert.Equal("The Battle", chapter.Title);
        Assert.Equal("https://comix.to/title/45z4-usemono-ari-no-houichi/5003", chapter.Url);
    }

    [Fact]
    public void BuildChapterList_PrefersSourceUrlFromApi_WhenAvailable()
    {
        var chapters = ComixScraper.BuildChapterList(
            "3ywnv-my-food-looks-very-cute",
            [
                new ComixScraper.ComixChapterCandidate(
                    ChapterId: 4388608,
                    Number: 158,
                    SourceUrl: "/title/3ywnv-my-food-looks-very-cute/4388608-chapter-158",
                    Name: "Test",
                    Votes: 1,
                    UpdatedAt: 1,
                    ScanlationGroupId: 0,
                    ScanlationGroupName: string.Empty,
                    IsOfficial: 0)
            ]);

        var chapter = Assert.Single(chapters);
        Assert.Equal("https://comix.to/title/3ywnv-my-food-looks-very-cute/4388608-chapter-158", chapter.Url);
    }
}
