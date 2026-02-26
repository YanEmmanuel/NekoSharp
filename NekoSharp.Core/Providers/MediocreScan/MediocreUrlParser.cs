using System.Text.RegularExpressions;

namespace NekoSharp.Core.Providers.MediocreScan;

internal enum MediocreUrlKind
{
    Unknown = 0,
    Obra = 1,
    Capitulo = 2
}

internal readonly record struct MediocreUrlRef(MediocreUrlKind Kind, int Id);

internal static partial class MediocreUrlParser
{
    [GeneratedRegex("^/obra/(?<id>\\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ObraRegex();

    [GeneratedRegex("^/capitulo/(?<id>\\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CapituloRegex();

    [GeneratedRegex("^/capitulos?/(?<id>\\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ApiCapituloRegex();

    public static bool TryParse(string? url, out MediocreUrlRef parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (int.TryParse(url, out var rawId) && rawId > 0)
        {
            parsed = new MediocreUrlRef(MediocreUrlKind.Capitulo, rawId);
            return true;
        }

        var hasAbsoluteUri = Uri.TryCreate(url, UriKind.Absolute, out var uri);
        if (hasAbsoluteUri)
        {
            var host = uri!.Host;
            var isKnownHost =
                host.Equals("mediocrescan.com", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("www.mediocrescan.com", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("api.mediocretoons.site", StringComparison.OrdinalIgnoreCase);

            if (!isKnownHost)
                return false;
        }
        else
        {
            if (!Uri.TryCreate($"https://mediocrescan.com{url}", UriKind.Absolute, out uri))
                return false;
        }

        var path = uri.AbsolutePath;

        var obraMatch = ObraRegex().Match(path);
        if (obraMatch.Success && int.TryParse(obraMatch.Groups["id"].Value, out var obraId) && obraId > 0)
        {
            parsed = new MediocreUrlRef(MediocreUrlKind.Obra, obraId);
            return true;
        }

        var chapterMatch = CapituloRegex().Match(path);
        if (chapterMatch.Success && int.TryParse(chapterMatch.Groups["id"].Value, out var chapterId) && chapterId > 0)
        {
            parsed = new MediocreUrlRef(MediocreUrlKind.Capitulo, chapterId);
            return true;
        }

        var apiMatch = ApiCapituloRegex().Match(path);
        if (apiMatch.Success && int.TryParse(apiMatch.Groups["id"].Value, out var apiChapterId) && apiChapterId > 0)
        {
            parsed = new MediocreUrlRef(MediocreUrlKind.Capitulo, apiChapterId);
            return true;
        }

        return false;
    }
}
