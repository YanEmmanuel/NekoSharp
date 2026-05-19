using NekoSharp.Core.Providers.Webtoons;
using Xunit;

namespace NekoSharp.Tests;

public class WebtoonsScraperTests
{
    [Fact]
    public void BuildEpisodeListApiUrl_UsesCanvasEndpointForCanvasSeries()
    {
        var parsed = new WebtoonsUrlRef(
            WebtoonsUrlKind.Series,
            WebtoonsSeriesType.Canvas,
            "en",
            304446,
            0,
            "https://www.webtoons.com/en/canvas/meme-girls/list?title_no=304446");

        var url = WebtoonsScraper.BuildEpisodeListApiUrl(parsed);

        Assert.Equal("https://m.webtoons.com/api/v1/canvas/304446/episodes?pageSize=99999", url);
    }

    [Fact]
    public void BuildChapterList_PreservesViewerLinkAndBgmFlag()
    {
        var chapters = WebtoonsScraper.BuildChapterList(
            [
                new WebtoonsEpisodeDto
                {
                    EpisodeTitle = "Episode 3",
                    ViewerLink = "/en/drama/test/viewer?title_no=10&episode_no=3",
                    ExposureDateMillis = 3,
                    HasBgm = true,
                },
                new WebtoonsEpisodeDto
                {
                    EpisodeTitle = "Episode 2",
                    ViewerLink = "/en/drama/test/viewer?title_no=10&episode_no=2",
                    ExposureDateMillis = 2,
                    HasBgm = false,
                }
            ]);

        Assert.Collection(
            chapters,
            chapter =>
            {
                Assert.Equal("Episode 3 ♫", chapter.Title);
                Assert.Equal(3d, chapter.Number);
                Assert.Equal("https://www.webtoons.com/en/drama/test/viewer?title_no=10&episode_no=3", chapter.Url);
            },
            chapter =>
            {
                Assert.Equal("Episode 2", chapter.Title);
                Assert.Equal(2d, chapter.Number);
                Assert.Equal("https://www.webtoons.com/en/drama/test/viewer?title_no=10&episode_no=2", chapter.Url);
            });
    }

    [Fact]
    public void BuildChapterList_WhenEpisodesAreMostlyUnrecognized_UsesSequentialNumbering()
    {
        var chapters = WebtoonsScraper.BuildChapterList(
            [
                new WebtoonsEpisodeDto { EpisodeTitle = "Finale", ViewerLink = "https://www.webtoons.com/1", ExposureDateMillis = 3 },
                new WebtoonsEpisodeDto { EpisodeTitle = "Interlude", ViewerLink = "https://www.webtoons.com/2", ExposureDateMillis = 2 },
                new WebtoonsEpisodeDto { EpisodeTitle = "Prologue", ViewerLink = "https://www.webtoons.com/3", ExposureDateMillis = 1 },
            ]);

        Assert.Equal([3d, 2d, 1d], chapters.Select(chapter => chapter.Number).ToArray());
    }
}
