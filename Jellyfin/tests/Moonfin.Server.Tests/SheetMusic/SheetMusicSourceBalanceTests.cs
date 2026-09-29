using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class SheetMusicSourceBalanceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "moonfin-score-balance-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _jobs = Path.Combine(Path.GetTempPath(), "moonfin-score-balance-jobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
        if (Directory.Exists(_jobs)) Directory.Delete(_jobs, recursive: true);
    }

    [Fact]
    public async Task BroadQuery_ReservesRoomForInternetArchive()
    {
        var entries = Enumerable.Range(0, 110).Select(index => new Dictionary<string, string>
        {
            ["id"] = "composer/work/score" + index,
            ["title"] = "Prelude " + index,
            ["composer"] = "Composer",
            ["license"] = "Public Domain",
            ["source_url"] = "https://www.mutopiaproject.org/ftp/score.ly",
            ["pdf_url"] = "https://www.mutopiaproject.org/ftp/score-a4.pdf",
        });
        File.WriteAllText(_file, JsonSerializer.Serialize(entries));
        var catalog = new SheetMusicCatalogService(_file, NullLogger<SheetMusicCatalogService>.Instance);
        using var client = new HttpClient(new ArchiveStub());
        var controller = new SheetMusicController(catalog, new InternetArchiveScoreService(client),
            new SheetMusicJobs(_jobs))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("Jellyfin-UserId", Guid.NewGuid().ToString())], "API-key")),
                },
            },
        };

        var result = Assert.IsType<OkObjectResult>(await controller.Search("Prelude", null, null));
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var pieces = body.RootElement.GetProperty("pieces");
        Assert.Equal(100, pieces.GetArrayLength());
        Assert.Contains(pieces.EnumerateArray(), x => x.GetProperty("source").GetString() == "internet_archive");
        Assert.True(body.RootElement.GetProperty("truncated").GetBoolean());
    }

    private sealed class ArchiveStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"response":{"docs":[{"identifier":"imslp-prelude-composer",
                       "title":"Prelude","creator":"Composer"}]}}
                    """, Encoding.UTF8, "application/json"),
            });
    }
}
