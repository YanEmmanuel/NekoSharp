using NekoSharp.Core.Models;

namespace NekoSharp.Core.Interfaces;

 
 
 
public interface IDownloadService
{
     
     
     
    Task DownloadChapterAsync(
        Manga manga,
        Chapter chapter,
        string outputDirectory,
        DownloadFormat format,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);

     
     
     
    Task DownloadChaptersAsync(
        Manga manga,
        IEnumerable<Chapter> chapters,
        string outputDirectory,
        DownloadFormat format,
        IProgress<(Chapter chapter, DownloadProgress progress)>? progress = null,
        CancellationToken ct = default);

     
     
     
    int MaxConcurrentDownloads { get; set; }
}
