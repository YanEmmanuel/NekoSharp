using NekoSharp.Core.Providers.Comix;
using Xunit;

namespace NekoSharp.Tests;

public class ComixScraperTests
{
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
                    ScanlationGroupId: 9275,
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
