namespace NekoSharp.Core.Models;

public class DownloadProgress
{
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public double Percentage => TotalPages > 0 ? (double)CurrentPage / TotalPages * 100 : 0;

    /// <summary>True when the chapter is in the SmartStitch post-processing phase.</summary>
    public bool IsStitching { get; set; }

    /// <summary>Human-readable status text during stitching (e.g. "Stitching...", "Slicing...").</summary>
    public string StitchingStatus { get; set; } = string.Empty;
}
