using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class SheetMusicJobsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "moonfin-score-jobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Enqueue_BindsStatusToOwnerAndDeduplicatesTheSameScore()
    {
        var jobs = new SheetMusicJobs(_folder);
        var piece = new SheetMusicPiece("mutopia:BachJS/score", "Prelude", "BachJS",
            null, "Piano", null, "Public Domain",
            "https://www.mutopiaproject.org/ftp/score.ly",
            "https://www.mutopiaproject.org/ftp/score-a4.pdf");

        var created = jobs.Enqueue("owner", piece);

        Assert.NotNull(created);
        Assert.Equal(created.Id, jobs.Enqueue("owner", piece)?.Id);
        Assert.Equal("queued", jobs.Find("owner", created.Id)?.Status);
        Assert.Null(jobs.Find("someone-else", created.Id));
        Assert.Null(jobs.Find("owner", "../config"));
    }

    [Fact]
    public void Enqueue_LimitsOneUserWithoutBlockingAnother()
    {
        var jobs = new SheetMusicJobs(_folder);
        SheetMusicPiece piece(int i) => new("mutopia:" + i, "Score " + i, "Composer",
            null, null, null, "Public Domain", "https://www.mutopiaproject.org/ftp/score.ly",
            "https://www.mutopiaproject.org/ftp/score-a4.pdf");

        Assert.NotNull(jobs.Enqueue("owner", piece(1)));
        Assert.NotNull(jobs.Enqueue("owner", piece(2)));
        Assert.Null(jobs.Enqueue("owner", piece(3)));
        Assert.NotNull(jobs.Enqueue("other-owner", piece(3)));
    }
}
