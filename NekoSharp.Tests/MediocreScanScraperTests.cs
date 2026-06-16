using System.Text.Json;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.MediocreScan;
using Xunit;

namespace NekoSharp.Tests;

public class MediocreScanScraperTests
{
    [Fact]
    public void HasNextChapterPage_UsesExplicitHasNextPageFlag()
    {
        using var doc = JsonDocument.Parse("""
        {
          "pagination": {
            "currentPage": 1,
            "totalPages": 3,
            "hasNextPage": false
          }
        }
        """);

        var hasNext = MediocreScanScraper.HasNextChapterPage(doc.RootElement, requestedPage: 1, itemCount: 100, pageSize: 100);

        Assert.False(hasNext);
    }

    [Fact]
    public void HasNextChapterPage_SupportsLocalizedPaginationFieldsFromExtension()
    {
        using var doc = JsonDocument.Parse("""
        {
          "pagination": {
            "pagina_atual": 2,
            "paginas": 4,
            "total": 113,
            "itens_por_pagina": 30
          }
        }
        """);

        var hasNext = MediocreScanScraper.HasNextChapterPage(doc.RootElement, requestedPage: 2, itemCount: 30, pageSize: 100);

        Assert.True(hasNext);
        Assert.Equal(113, MediocreScanScraper.GetExpectedChapterCount(doc.RootElement));
    }

    [Fact]
    public void MergeChapterArray_MergesEmbeddedFallbackChapters()
    {
        using var doc = JsonDocument.Parse("""
        {
          "capitulos": [
            { "id": 287905, "nome": "113", "numero": 113 },
            { "id": 287904, "nome": "112", "numero": 112 },
            { "id": 0, "nome": "ignorar", "numero": 0 }
          ]
        }
        """);

        var chaptersById = new Dictionary<int, Chapter>
        {
            [287905] = new()
            {
                Title = "113",
                Number = 113,
                Url = "https://mediocrescan.com/capitulo/287905"
            }
        };

        var merged = MediocreScanScraper.MergeChapterArray(doc.RootElement.GetProperty("capitulos"), chaptersById);

        Assert.Equal(2, merged);
        Assert.Equal(2, chaptersById.Count);
        Assert.Equal(112, chaptersById[287904].Number);
        Assert.Equal("https://mediocrescan.com/capitulo/287904", chaptersById[287904].Url);
    }

    [Fact]
    public void MergeChapterArray_SupportsCurrentBackApiChapterFields()
    {
        using var doc = JsonDocument.Parse("""
        {
          "capitulos": [
            {
              "cap_id": 518274,
              "cap_nome": "Capítulo 124",
              "cap_num": 124,
              "cap_tipo": "imagem"
            },
            {
              "cap_id": 517927,
              "cap_nome": "Capítulo 123",
              "cap_num": 123,
              "cap_tipo": "imagem"
            }
          ]
        }
        """);

        var chaptersById = new Dictionary<int, Chapter>();

        var merged = MediocreScanScraper.MergeChapterArray(doc.RootElement.GetProperty("capitulos"), chaptersById);

        Assert.Equal(2, merged);
        Assert.Equal(2, chaptersById.Count);
        Assert.Equal("Capítulo 124", chaptersById[518274].Title);
        Assert.Equal(124, chaptersById[518274].Number);
        Assert.Equal("https://mediocrescan.com/capitulo/518274", chaptersById[518274].Url);
    }

    [Fact]
    public void MapPageArray_SupportsCurrentCdnManifestFields()
    {
        using var doc = JsonDocument.Parse("""
        [
          {
            "url": "obras/259/capitulos/124/bc699ffa7ad00dd9bb85890246d399607a01f6b4.webp",
            "ordem": 3
          },
          {
            "url": "obras/259/capitulos/124/da6bd4e5153804d99585f2d687725a445031475b.webp",
            "ordem": 1
          }
        ]
        """);

        var pages = MediocreScanScraper.MapPageArray(doc.RootElement, obraId: 259, chapterFolder: "124");

        Assert.Equal(2, pages.Count);
        Assert.Equal(1, pages[0].Number);
        Assert.Equal("https://cdn.mediocrescan.com/obras/259/capitulos/124/da6bd4e5153804d99585f2d687725a445031475b.webp", pages[0].ImageUrl);
        Assert.Equal("https://cdn.mediocrescan.com/obras/259/capitulos/124/bc699ffa7ad00dd9bb85890246d399607a01f6b4.webp", pages[1].ImageUrl);
        Assert.All(pages, page => Assert.Equal("https://mediocrescan.com/", page.RefererUrl));
    }

    [Fact]
    public void MapPageArray_SupportsLegacyInlinePageSrcFields()
    {
        using var doc = JsonDocument.Parse("""
        [
          { "src": "001.webp" },
          { "src": "002.webp" }
        ]
        """);

        var pages = MediocreScanScraper.MapPageArray(doc.RootElement, obraId: 259, chapterFolder: "124");

        Assert.Equal(2, pages.Count);
        Assert.Equal("https://cdn.mediocrescan.com/obras/259/capitulos/124/001.webp", pages[0].ImageUrl);
        Assert.Equal("https://cdn.mediocrescan.com/obras/259/capitulos/124/002.webp", pages[1].ImageUrl);
    }

    [Fact]
    public void CreateBodyPreview_TruncatesCloudflareChallengeHtml()
    {
        var body = "<!DOCTYPE html><html><head><title>Just a moment...</title></head><body>" +
                   new string('x', 2000) +
                   "</body></html>";

        var preview = MediocreScanScraper.CreateBodyPreview(body);

        Assert.Contains("Just a moment", preview);
        Assert.Contains("omitido", preview);
        Assert.True(preview.Length < body.Length);
    }
}
