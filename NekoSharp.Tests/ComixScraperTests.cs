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
        var relativeUrl = ComixScraper.BuildChapterListRelativeUrl("5vwvl", 2);

        Assert.Equal(
            "manga/5vwvl/chapters?order%5Bnumber%5D=desc&limit=100&page=2&time=1&_=" +
            "xQm9tJfLwGhz_0Eq8S_YAHYkwp-q1PLfm50W5QJnyd1NnNYpAjXjyCoAzoOLrrymJN0xWS0NeDGz_rNrbqBjLLP1H9qi",
            relativeUrl);
    }

    [Fact]
    public void HttpClient_ResolvesFullUrlWithHashParams()
    {
        using var http = new System.Net.Http.HttpClient
        {
            BaseAddress = new System.Uri("https://comix.to/api/v2/")
        };
        var relativeUrl = ComixScraper.BuildChapterListRelativeUrl("r9qjm", 1);
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, relativeUrl);
        var resolvedUri = new System.Uri(http.BaseAddress!, request.RequestUri!);
        var absString = resolvedUri.AbsoluteUri;

        Assert.Contains("&time=1", absString);
        Assert.Contains("&_=", absString);
        Assert.DoesNotContain("%255B", absString);
        Assert.DoesNotContain("%255D", absString);
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
                    Name: "United Front",
                    Votes: 10,
                    UpdatedAt: 200,
                    ScanlationGroupId: 10702,
                    ScanlationGroupName: string.Empty,
                    IsOfficial: 1),
                new ComixScraper.ComixChapterCandidate(
                    ChapterId: 5002,
                    Number: 76,
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
}
