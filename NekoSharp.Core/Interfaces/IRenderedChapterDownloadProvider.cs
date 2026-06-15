using NekoSharp.Core.Models;

namespace NekoSharp.Core.Interfaces;

public interface IRenderedChapterDownloadProvider
{
    Task<IReadOnlyDictionary<int, RenderedPageDownload>> TryRenderChapterPagesAsync(
        Chapter chapter,
        IReadOnlyList<Page> pages,
        CancellationToken ct = default);
}
