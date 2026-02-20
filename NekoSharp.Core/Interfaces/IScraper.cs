using NekoSharp.Core.Models;

namespace NekoSharp.Core.Interfaces;

 
 
 
 
public interface IScraper
{
     
     
     
    string Name { get; }

     
     
     
    string BaseUrl { get; }

     
     
     
    bool CanHandle(string url);

     
     
     
    Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default);

     
     
     
    Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default);

     
     
     
    Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default);
}
