using NekoSharp.Core.Providers.MediocreScan;
using Xunit;

namespace NekoSharp.Tests;

public class MediocreUrlParserTests
{
    [Theory]
    [InlineData("https://mediocrescan.com/obra/198", 1, 198)]
    [InlineData("https://mediocrescan.com/obra/198/", 1, 198)]
    [InlineData("https://mediocrescan.com/capitulo/287905", 2, 287905)]
    [InlineData("https://api.mediocretoons.site/capitulos/287905", 2, 287905)]
    public void TryParse_ValidUrls_ReturnsExpected(string url, int kind, int id)
    {
        var ok = MediocreUrlParser.TryParse(url, out var parsed);

        Assert.True(ok);
        Assert.Equal(kind, (int)parsed.Kind);
        Assert.Equal(id, parsed.Id);
    }

    [Theory]
    [InlineData("https://mediocrescan.com/")]
    [InlineData("https://mediocrescan.com/foo/bar")]
    [InlineData("https://example.com/obra/1")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_InvalidUrls_ReturnsFalse(string? url)
    {
        var ok = MediocreUrlParser.TryParse(url, out var parsed);

        Assert.False(ok);
        Assert.Equal(MediocreUrlKind.Unknown, parsed.Kind);
        Assert.Equal(0, parsed.Id);
    }
}
