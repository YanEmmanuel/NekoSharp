using NekoSharp.Core.Providers.FlowerMangaDotNet;
using Xunit;

namespace NekoSharp.Tests;

public sealed class FlowerMangaDotNetScraperTests
{
    [Fact]
    public void ParseMkChapters_ReadsCurrentChapterData()
    {
        var chapters = FlowerMangaDotNetScraper.ParseMkChapters("""
            {"items":[
              {"num":"77","name":"Capítulo 77","url":"https://flowermanga.org/manga/teste/capitulo-77/"},
              {"num":"76.5","name":"Capítulo 76.5","url":"https://flowermanga.org/manga/teste/capitulo-76-5/"}
            ]}
            """);

        Assert.Collection(chapters,
            chapter =>
            {
                Assert.Equal(77, chapter.Number);
                Assert.Equal("Capítulo 77", chapter.Title);
            },
            chapter => Assert.Equal(76.5, chapter.Number));
    }
}
