using System.Text.RegularExpressions;

namespace NekoSharp.Core.Providers.Webtoons;

internal enum WebtoonsUrlKind
{
    Unknown = 0,
    Series = 1,
    Episode = 2,
}

internal enum WebtoonsSeriesType
{
    Webtoon = 0,
    Canvas = 1,
}

internal readonly record struct WebtoonsUrlRef(
    WebtoonsUrlKind Kind,
    WebtoonsSeriesType SeriesType,
    string LanguageCode,
    long TitleId,
    long EpisodeId,
    string AbsoluteUrl);

internal static partial class WebtoonsUrlParser
{
    [GeneratedRegex("^[a-z]{2}(?:-[a-z]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LangSegmentRegex();

    public static bool TryParse(string? url, out WebtoonsUrlRef parsed)
    {
        parsed = new WebtoonsUrlRef(
            WebtoonsUrlKind.Unknown,
            WebtoonsSeriesType.Webtoon,
            string.Empty,
            0,
            0,
            string.Empty);

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!TryCreateWebtoonsUri(url.Trim(), out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("webtoons.com" or "www.webtoons.com" or "m.webtoons.com"))
            return false;

        var titleIdRaw = GetQueryValue(uri, "title_no") ?? GetQueryValue(uri, "titleNo");
        if (!long.TryParse(titleIdRaw, out var titleId) || titleId <= 0)
            return false;

        var pathSegments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathSegments.Length == 0)
            return false;

        var first = pathSegments[0];
        var languageCode = LangSegmentRegex().IsMatch(first) ? first.ToLowerInvariant() : string.Empty;
        var seriesType = pathSegments.Any(segment => segment.Equals("canvas", StringComparison.OrdinalIgnoreCase)) ||
            pathSegments.Any(segment => segment.Equals("challenge", StringComparison.OrdinalIgnoreCase))
                ? WebtoonsSeriesType.Canvas
                : WebtoonsSeriesType.Webtoon;

        var episodeNoRaw = GetQueryValue(uri, "episode_no") ?? GetQueryValue(uri, "episodeNo");
        var episodeId = long.TryParse(episodeNoRaw, out var parsedEpisodeId) ? parsedEpisodeId : 0;
        var kind = episodeId > 0 ||
            pathSegments.Any(segment => segment.Equals("viewer", StringComparison.OrdinalIgnoreCase)) ||
            pathSegments.Any(segment => segment.Equals("episode", StringComparison.OrdinalIgnoreCase))
                ? WebtoonsUrlKind.Episode
                : WebtoonsUrlKind.Series;

        parsed = new WebtoonsUrlRef(
            kind,
            seriesType,
            languageCode,
            titleId,
            episodeId,
            uri.ToString());
        return true;
    }

    private static bool TryCreateWebtoonsUri(string url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!))
            return true;

        if (url.StartsWith('/'))
            return Uri.TryCreate($"https://www.webtoons.com{url}", UriKind.Absolute, out uri!);

        return false;
    }

    private static string? GetQueryValue(Uri uri, string name)
    {
        if (string.IsNullOrWhiteSpace(uri.Query))
            return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var rawName = separator >= 0 ? pair[..separator] : pair;
            if (!rawName.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var rawValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            return Uri.UnescapeDataString(rawValue);
        }

        return null;
    }
}
