namespace NekoSharp.Core.Models;

public class Page
{
    public int Number { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public string? LocalPath { get; set; }
}
