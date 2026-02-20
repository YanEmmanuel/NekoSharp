namespace NekoSharp.Core.Models;

public class Chapter
{
    public string Title { get; set; } = string.Empty;
    public double Number { get; set; }
    public string Url { get; set; } = string.Empty;
    public List<Page> Pages { get; set; } = [];
}
