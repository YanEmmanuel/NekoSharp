using Xunit;
using NekoSharp.Core.Helpers;

namespace NekoSharp.Tests;

public class ChapterHelperTests
{
    [Theory]
    [InlineData("Chapter 12", 12)]
    [InlineData("Vol. 3 Chapter 4", 3)]
    [InlineData("Capítulo 12.5 - Title", 12.5)]
    [InlineData("Capítulo 12,5 - Title", 12.5)]
    [InlineData("No number here", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("Episode 001 - Part 2", 1)]
    public void ExtractChapterNumber_Works(string? input, double expected)
    {
        var actual = ChapterHelper.ExtractChapterNumber(input);
        Assert.Equal(expected, actual, 6);
    }
}
