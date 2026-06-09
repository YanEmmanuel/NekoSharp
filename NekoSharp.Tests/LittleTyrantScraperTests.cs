using NekoSharp.Core.Providers.LittleTyrant;
using Xunit;

namespace NekoSharp.Tests;

public sealed class LittleTyrantScraperTests
{
    [Fact]
    public void ExtractPagesFromReaderScripts_DecodesBase64ImageUrls()
    {
        var scripts = new[]
        {
            """
            (function() {
                var pages = ["IGh0dHBzOi8vY2RuLnRpcmFuaW5oYS53b3JsZC9hLzAwLmpwZw==","IGh0dHBzOi8vY2RuLnRpcmFuaW5oYS53b3JsZC9hLzAxLmpwZw=="];
            })();
            """
        };

        var pages = LittleTyrantScraper.ExtractPagesFromReaderScripts(
            scripts,
            "https://tiraninha.world/manga/teste/54/");

        Assert.Equal(2, pages.Count);
        Assert.Equal("https://cdn.tiraninha.world/a/00.jpg", pages[0].ImageUrl);
        Assert.Equal("https://cdn.tiraninha.world/a/01.jpg", pages[1].ImageUrl);
        Assert.All(pages, page => Assert.Equal("https://tiraninha.world/manga/teste/54/", page.RefererUrl));
    }

    [Fact]
    public void ParseLoadMorePayload_ParsesHtmlAndPaginationFlags()
    {
        const string json = """
            {
              "success": true,
              "data": {
                "html": "<li class=\"wp-manga-chapter\"><a href=\"https://tiraninha.world/manga/teste/42/\" class=\"mc-chapter-link\"><span class=\"mc-chapter-title\">42</span></a></li>",
                "has_more": true,
                "new_offset": 24
              }
            }
            """;

        var payload = LittleTyrantScraper.ParseLoadMorePayload(json);

        Assert.NotNull(payload);
        Assert.Contains("/42/", payload.Value.Html);
        Assert.True(payload.Value.HasMore);
        Assert.Equal(24, payload.Value.NewOffset);
    }

    [Fact]
    public void MergeCookieHeader_PreservesExistingProviderCookies()
    {
        var merged = LittleTyrantScraper.MergeCookieHeader(
            "wordpress_logged_in_hash=abc123; wordpress_sec_hash=def456",
            new Dictionary<string, string>
            {
                ["cf_clearance"] = "clear789",
                ["__cf_bm"] = "bm999"
            });

        Assert.Contains("wordpress_logged_in_hash=abc123", merged);
        Assert.Contains("wordpress_sec_hash=def456", merged);
        Assert.Contains("cf_clearance=clear789", merged);
        Assert.Contains("__cf_bm=bm999", merged);
    }

    [Fact]
    public void MergeCookieHeader_OverridesDuplicateNamesWithIncomingCookies()
    {
        var merged = LittleTyrantScraper.MergeCookieHeader(
            "cf_clearance=old; wordpress_logged_in_hash=abc123",
            new Dictionary<string, string>
            {
                ["cf_clearance"] = "new",
            });

        var parsed = merged
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment => segment.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        Assert.Equal("new", parsed["cf_clearance"]);
        Assert.Equal("abc123", parsed["wordpress_logged_in_hash"]);
    }
}
