using System.Text.RegularExpressions;

namespace NekoSharp.Core.Providers.Comix;

internal enum ComixUrlKind
{
    Unknown = 0,
    Manga = 1,
    Chapter = 2
}

internal readonly record struct ComixUrlRef(
    ComixUrlKind Kind,
    string HashId,
    string MangaSegment,
    int ChapterId);

internal static partial class ComixUrlParser
{
    [GeneratedRegex("^/title/(?<manga>[^/?#]+)(?:/(?<chapter>\\d+)(?:-[^/?#]*)?)?(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("^[a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex HashRegex();

    public static bool TryParse(string? url, out ComixUrlRef parsed)
    {
        parsed = new ComixUrlRef(ComixUrlKind.Unknown, string.Empty, string.Empty, 0);
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var trimmed = url.Trim();
        Uri? uri;
        if (trimmed.StartsWith('/') || !trimmed.Contains("://", StringComparison.Ordinal))
        {
            var normalized = trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";
            if (!Uri.TryCreate($"https://comix.to{normalized}", UriKind.Absolute, out uri))
                return false;
        }
        else if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            var host = absolute.Host;
            var isKnownHost =
                host.Equals("comix.to", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("www.comix.to", StringComparison.OrdinalIgnoreCase);

            if (!isKnownHost)
                return false;

            uri = absolute;
        }
        else
            return false;

        var match = TitleRegex().Match(uri.AbsolutePath);
        if (!match.Success)
            return false;

        var mangaSegment = match.Groups["manga"].Value.Trim();
        var hashId = ExtractHashId(mangaSegment);
        if (string.IsNullOrWhiteSpace(hashId))
            return false;

        var chapterGroup = match.Groups["chapter"].Value;
        var chapterId = int.TryParse(chapterGroup, out var parsedChapterId) ? parsedChapterId : 0;
        var kind = chapterId > 0 ? ComixUrlKind.Chapter : ComixUrlKind.Manga;

        parsed = new ComixUrlRef(kind, hashId, mangaSegment, chapterId);
        return true;
    }

    internal static string ExtractHashId(string? mangaSegment)
    {
        if (string.IsNullOrWhiteSpace(mangaSegment))
            return string.Empty;

        var match = HashRegex().Match(mangaSegment.Trim());
        return match.Success ? match.Value.ToLowerInvariant() : string.Empty;
    }
}
