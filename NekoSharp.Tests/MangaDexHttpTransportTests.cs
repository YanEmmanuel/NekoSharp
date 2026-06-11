using System.Net;
using System.Net.Sockets;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class MangaDexHttpTransportTests
{
    [Theory]
    [InlineData("api.mangadex.org", true)]
    [InlineData("node.mangadex.network", true)]
    [InlineData("mangadex.org", true)]
    [InlineData("mangadex.network", true)]
    [InlineData("mangadex.org.example.com", false)]
    [InlineData("example.com", false)]
    public void IsMangaDexHost_OnlyMatchesMangaDexDomains(string host, bool expected)
    {
        Assert.Equal(expected, MangaDexHttpTransport.IsMangaDexHost(host));
    }

    [Fact]
    public async Task ResolveMangaDexAddressesAsync_WhenSystemDnsFails_UsesFallback()
    {
        var expected = IPAddress.Parse("104.26.2.73");
        var fallbackCalls = 0;

        var addresses = await MangaDexHttpTransport.ResolveMangaDexAddressesAsync(
            "cdn.mangadex.network",
            CancellationToken.None,
            systemResolver: (_, _) => throw new SocketException((int)SocketError.HostNotFound),
            fallbackResolver: (_, _) =>
            {
                fallbackCalls++;
                return Task.FromResult(new[] { expected });
            });

        Assert.Equal(1, fallbackCalls);
        Assert.Equal(new[] { expected }, addresses);
    }

    [Fact]
    public void ParseDnsOverHttpsResponse_ReturnsOnlyUniqueIpv4Answers()
    {
        const string json = """
            {
              "Status": 0,
              "Answer": [
                { "name": "cdn.mangadex.network", "type": 5, "data": "alias.example" },
                { "name": "cdn.mangadex.network", "type": 1, "data": "104.26.2.73" },
                { "name": "cdn.mangadex.network", "type": 1, "data": "104.26.2.73" },
                { "name": "cdn.mangadex.network", "type": 28, "data": "2606:4700:20::681a:249" }
              ]
            }
            """;

        var addresses = MangaDexHttpTransport.ParseDnsOverHttpsResponse(json);

        Assert.Equal(new[] { IPAddress.Parse("104.26.2.73") }, addresses);
    }
}
