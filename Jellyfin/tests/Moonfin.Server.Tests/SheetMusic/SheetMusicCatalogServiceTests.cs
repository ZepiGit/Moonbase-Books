using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class SheetMusicCatalogServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sheetmusic-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void Pieces_LoadsValidEntriesWithProvenance()
    {
        File.WriteAllText(_path, """
            [
              {
                "id": "BachJS/BWV999/Bach_Prelude_BWV999",
                "title": "Prelude in D Minor",
                "composer": "BachJS",
                "opus": "BWV 999",
                "instrument": "Lute, Guitar",
                "style": "Baroque",
                "license": "Public Domain",
                "source_url": "https://www.mutopiaproject.org/ftp/BachJS/BWV999/Bach_Prelude_BWV999/Bach_Prelude_BWV999.ly",
                "pdf_url": "https://www.mutopiaproject.org/ftp/BachJS/BWV999/Bach_Prelude_BWV999/Bach_Prelude_BWV999-a4.pdf"
              }
            ]
            """);

        var service = new SheetMusicCatalogService(_path, NullLogger<SheetMusicCatalogService>.Instance);

        var piece = Assert.Single(service.Pieces);
        Assert.Equal("BachJS/BWV999/Bach_Prelude_BWV999", piece.Id);
        Assert.Equal("Prelude in D Minor", piece.Title);
        Assert.Equal("BachJS", piece.Composer);
        Assert.Equal("BWV 999", piece.Opus);
        Assert.Equal("Lute, Guitar", piece.Instrument);
        Assert.Equal("Public Domain", piece.License);
        Assert.Contains("mutopiaproject.org", piece.SourceUrl);
        Assert.EndsWith("-a4.pdf", piece.PdfUrl);
    }

    [Fact]
    public void Pieces_SkipsEntriesMissingRequiredFields()
    {
        File.WriteAllText(_path, """
            [
              {"id": "x", "title": "t", "composer": "c", "license": "Public Domain"},
              {"id": "y", "title": "t", "composer": "c", "license": "Public Domain", "source_url": "https://www.mutopiaproject.org/ftp/y.ly", "pdf_url": "https://www.mutopiaproject.org/ftp/y-a4.pdf"},
              {"id": "z", "title": "", "composer": "c", "license": "Public Domain", "source_url": "https://www.mutopiaproject.org/ftp/z.ly", "pdf_url": "https://www.mutopiaproject.org/ftp/z-a4.pdf"}
            ]
            """);

        var service = new SheetMusicCatalogService(_path, NullLogger<SheetMusicCatalogService>.Instance);

        var piece = Assert.Single(service.Pieces);
        Assert.Equal("y", piece.Id);
    }

    [Fact]
    public void Pieces_MissingFileYieldsEmptyCatalog()
    {
        var service = new SheetMusicCatalogService(_path, NullLogger<SheetMusicCatalogService>.Instance);

        Assert.Empty(service.Pieces);
    }

    [Fact]
    public void Pieces_RootMustBeAnArray()
    {
        File.WriteAllText(_path, "{\"pieces\": []}");

        var service = new SheetMusicCatalogService(_path, NullLogger<SheetMusicCatalogService>.Instance);

        Assert.Empty(service.Pieces);
    }

    [Fact]
    public void Pieces_ReloadsWhenFileChanges()
    {
        File.WriteAllText(_path, "[]");
        var service = new SheetMusicCatalogService(_path, NullLogger<SheetMusicCatalogService>.Instance);
        Assert.Empty(service.Pieces);

        File.WriteAllText(_path, """
            [{"id": "a", "title": "T", "composer": "C", "license": "Public Domain", "source_url": "https://www.mutopiaproject.org/ftp/a.ly", "pdf_url": "https://www.mutopiaproject.org/ftp/a-a4.pdf"}]
            """);
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(1));

        Assert.Single(service.Pieces);
    }

    [Fact]
    public void Pieces_RejectsExternalPdfUrl()
    {
        File.WriteAllText(_path, """
            [{"id":"x","title":"T","composer":"C","license":"Public Domain",
              "source_url":"https://www.mutopiaproject.org/ftp/x.ly",
              "pdf_url":"https://example.invalid/x.pdf"}]
            """);

        var service = new SheetMusicCatalogService(_path, NullLogger<SheetMusicCatalogService>.Instance);
        Assert.Empty(service.Pieces);
    }
}
