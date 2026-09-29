using System.Net;
using System.Text;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class InternetArchiveScoreServiceTests
{
    [Fact]
    public async Task SearchAndResolve_UseFixedCollectionAndOriginalPdf()
    {
        var stub = new Stub();
        using var client = new HttpClient(stub);
        var service = new InternetArchiveScoreService(client);

        var results = await service.SearchAsync("Danse rustique", "Mason", CancellationToken.None);
        var row = Assert.Single(results);
        Assert.Equal("ia:imslp-rustique-op16-mason-william", row.Id);
        Assert.Contains("collection%3A%28imslp%29", stub.Urls[0]);
        Assert.Contains("title%3A", stub.Urls[0]);

        var detail = await service.ResolveAsync("imslp-rustique-op16-mason-william", CancellationToken.None);
        Assert.NotNull(detail);
        Assert.EndsWith("score.pdf", detail.PdfUrl);
        Assert.DoesNotContain("_text.pdf", detail.PdfUrl);
        Assert.Equal("https://archive.org/details/imslp-rustique-op16-mason-william", detail.SourceUrl);
        Assert.Null(await service.ResolveAsync("../admin", CancellationToken.None));
        Assert.Equal(2, stub.Urls.Count);
    }

    private sealed class Stub : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            var json = url.Contains("advancedsearch", StringComparison.Ordinal) ? """
                {"response":{"docs":[{"identifier":"imslp-rustique-op16-mason-william",
                    "title":"Danse rustique","creator":"Mason, William"}]}}
                """ : """
                {"metadata":{"title":"Danse rustique","creator":"Mason, William",
                    "collection":["imslp"]},"files":[
                    {"name":"score_text.pdf","source":"derivative","size":"1200"},
                    {"name":"score.pdf","source":"original","size":"791463"}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
