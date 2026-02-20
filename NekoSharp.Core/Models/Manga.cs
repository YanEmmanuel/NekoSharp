namespace NekoSharp.Core.Models;

public class Manga
{
    public string Name { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public List<Chapter> Chapters { get; set; } = [];
}
