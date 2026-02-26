using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

public static class DownloadPaths
{
    public static string GetMangaDirectory(string outputDirectory, Manga manga)
    {
        var safeMangaName = SanitizeFileName(manga.Name);
        return Path.Combine(outputDirectory, safeMangaName);
    }

    public static string GetChapterDirectory(string outputDirectory, Manga manga, Chapter chapter)
    {
        var safeMangaName = SanitizeFileName(manga.Name);
        var safeChapterName = SanitizeFileName($"Capitulo {chapter.Number:000} - {chapter.Title}");
        return Path.Combine(outputDirectory, safeMangaName, safeChapterName);
    }

    public static string GetChapterArchivePath(string outputDirectory, Manga manga, Chapter chapter, string extension)
    {
        var safeMangaName = SanitizeFileName(manga.Name);
        var safeChapterName = SanitizeFileName($"Capitulo {chapter.Number:000} - {chapter.Title}");
        var fileName = $"{safeChapterName}{extension}";
        return Path.Combine(outputDirectory, safeMangaName, fileName);
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return sanitized.Trim().TrimEnd('.');
    }
}
