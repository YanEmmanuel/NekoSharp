using NekoSharp.Core.Providers.Comix;
using Xunit;

namespace NekoSharp.Tests;

public class ComixUrlParserTests
{
    [Theory]
    [InlineData("https://comix.to/title/45z4", 1, "45z4", "45z4", 0)]
    [InlineData("https://comix.to/title/45z4-usemono-ari-no-houichi", 1, "45z4", "45z4-usemono-ari-no-houichi", 0)]
    [InlineData("https://comix.to/title/45z4/3536869", 2, "45z4", "45z4", 3536869)]
    [InlineData("https://comix.to/title/45z4-usemono-ari-no-houichi/3536869-chapter-19", 2, "45z4", "45z4-usemono-ari-no-houichi", 3536869)]
    [InlineData("/title/45z4-usemono-ari-no-houichi/3536869-chapter-19", 2, "45z4", "45z4-usemono-ari-no-houichi", 3536869)]
    public void TryParse_ValidUrls_ReturnsExpected(string url, int kind, string hashId, string mangaSegment, int chapterId)
    {
        var ok = ComixUrlParser.TryParse(url, out var parsed);

        Assert.True(ok);
        Assert.Equal(kind, (int)parsed.Kind);
        Assert.Equal(hashId, parsed.HashId);
        Assert.Equal(mangaSegment, parsed.MangaSegment);
        Assert.Equal(chapterId, parsed.ChapterId);
    }

    [Theory]
    [InlineData("https://comix.to/")]
    [InlineData("https://comix.to/browser")]
    [InlineData("https://example.com/title/45z4")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_InvalidUrls_ReturnsFalse(string? url)
    {
        var ok = ComixUrlParser.TryParse(url, out var parsed);

        Assert.False(ok);
        Assert.Equal(ComixUrlKind.Unknown, parsed.Kind);
        Assert.Equal(string.Empty, parsed.HashId);
        Assert.Equal(string.Empty, parsed.MangaSegment);
        Assert.Equal(0, parsed.ChapterId);
    }
}
