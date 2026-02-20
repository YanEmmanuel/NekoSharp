using System.Globalization;
using System.Text.RegularExpressions;

namespace NekoSharp.Core.Helpers;

public static class ChapterHelper
{
     
     
     
     
     
    public static double ExtractChapterNumber(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return 0;

         
        var match = Regex.Match(title, "(\\d+([.,]\\d+)?)");
        if (!match.Success)
            return 0;

        var value = match.Value.Replace(',', '.');
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            return number;

        return 0;
    }
}
