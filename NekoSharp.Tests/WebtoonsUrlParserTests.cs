using NekoSharp.Core.Providers.Webtoons;
using Xunit;

namespace NekoSharp.Tests;

public class WebtoonsUrlParserTests
{
    [Theory]
    [InlineData("https://www.webtoons.com/en/drama/odd-girl-out/list?title_no=1420", 1, 0, "en", 1420, 0)]
    [InlineData("https://www.webtoons.com/en/canvas/meme-girls/list?title_no=304446", 1, 1, "en", 304446, 0)]
    [InlineData("https://www.webtoons.com/episodeList?titleNo=1049", 1, 0, "", 1049, 0)]
    [InlineData("https://www.webtoons.com/challenge/episodeList?titleNo=304446", 1, 1, "", 304446, 0)]
    [InlineData("https://www.webtoons.com/en/drama/odd-girl-out/viewer?title_no=1420&episode_no=1", 2, 0, "en", 1420, 1)]
    [InlineData("https://m.webtoons.com/en/canvas/meme-girls/viewer?title_no=304446&episode_no=25", 2, 1, "en", 304446, 25)]
    public void TryParse_ValidUrls_ReturnsExpected(
        string url,
        int kind,
        int seriesType,
        string languageCode,
        long titleId,
        long episodeId)
    {
        var ok = WebtoonsUrlParser.TryParse(url, out var parsed);

        Assert.True(ok);
        Assert.Equal(kind, (int)parsed.Kind);
        Assert.Equal(seriesType, (int)parsed.SeriesType);
        Assert.Equal(languageCode, parsed.LanguageCode);
        Assert.Equal(titleId, parsed.TitleId);
        Assert.Equal(episodeId, parsed.EpisodeId);
    }

    [Theory]
    [InlineData("https://www.webtoons.com/en/")]
    [InlineData("https://example.com/en/drama/test/list?title_no=1")]
    [InlineData("https://www.webtoons.com/en/drama/test/list")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_InvalidUrls_ReturnsFalse(string? url)
    {
        var ok = WebtoonsUrlParser.TryParse(url, out var parsed);

        Assert.False(ok);
        Assert.Equal(WebtoonsUrlKind.Unknown, parsed.Kind);
        Assert.Equal(0, parsed.TitleId);
        Assert.Equal(0, parsed.EpisodeId);
    }
}
