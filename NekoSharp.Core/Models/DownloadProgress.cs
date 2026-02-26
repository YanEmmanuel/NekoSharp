namespace NekoSharp.Core.Models;

public class DownloadProgress
{
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public double Percentage => TotalPages > 0 ? (double)CurrentPage / TotalPages * 100 : 0;

    public bool IsStitching { get; set; }

    public string StitchingStatus { get; set; } = string.Empty;
}
