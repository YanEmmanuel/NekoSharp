using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Helpers;

public static class ChapterKeyHelper
{
    public static string BuildChapterKey(string providerKey, Chapter chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var normalizedUrl = NormalizeUrl(chapter.Url);
        if (!string.IsNullOrWhiteSpace(normalizedUrl))
            return normalizedUrl;

        var seed = string.Join("|",
            providerKey.Trim().ToLowerInvariant(),
            chapter.Title.Trim().ToLowerInvariant(),
            chapter.Number.ToString("0.####################", CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"hash:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static string BuildMangaIdentityKey(string providerKey, string mangaUrl)
    {
        var normalizedUrl = NormalizeUrl(mangaUrl);
        if (!string.IsNullOrWhiteSpace(normalizedUrl))
            return normalizedUrl;

        var seed = string.Join("|",
            providerKey.Trim().ToLowerInvariant(),
            mangaUrl.Trim().ToLowerInvariant());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"hash:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static string NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var raw = input.Trim();
        var hashIndex = raw.IndexOf('#');
        if (hashIndex >= 0)
            raw = raw[..hashIndex];

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return TrimTrailingSlash(raw);

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var portPart = IsDefaultPort(scheme, uri.Port) || uri.Port < 0
            ? string.Empty
            : $":{uri.Port}";

        var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        path = path != "/" ? path.TrimEnd('/') : path;
        if (string.IsNullOrWhiteSpace(path))
            path = "/";

        var query = uri.Query;
        if (query == "?")
            query = string.Empty;

        return $"{scheme}://{host}{portPart}{path}{query}";
    }

    private static bool IsDefaultPort(string scheme, int port)
    {
        return (scheme == "http" && port == 80) ||
               (scheme == "https" && port == 443);
    }

    private static string TrimTrailingSlash(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        return input != "/" ? input.TrimEnd('/') : input;
    }
}
