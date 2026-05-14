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
}
