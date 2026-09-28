using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Pins the two trust boundaries of the Books proxy: the controller can only ever
/// construct the five whitelisted upstream paths (so a crafted query can't turn one
/// endpoint into an arbitrary path proxy), and the outgoing request carries exactly the
/// identity the plugin computed from Jellyfin claims - incoming identity headers never
/// reach Shelfmark.
/// </summary>
public sealed class ShelfmarkProxyServiceTests
{
    private static ShelfmarkProxyService CreateService() =>
        new(new TestHttpClientFactory(), NullLogger<ShelfmarkProxyService>.Instance);

    [Fact]
    public void BuildUpstreamPath_KeepsOperationsOnTheirWhitelistedPaths()
    {
        var service = CreateService();

        Assert.Equal("/api/status", service.BuildUpstreamPath("status", new Dictionary<string, string?>()));
        Assert.Equal("/api/metadata/search?query=tolkien",
            service.BuildUpstreamPath("search", new Dictionary<string, string?> { ["query"] = "tolkien" }));
        Assert.Equal("/api/releases?book_id=42&provider=openlibrary&source=prowlarr",
            service.BuildUpstreamPath("releases", new Dictionary<string, string?>
            {
                ["provider"] = "openlibrary",
                ["book_id"] = "42",
            }));
        Assert.Equal("/api/downloads/active", service.BuildUpstreamPath("active", new Dictionary<string, string?>()));
    }

    [Theory]
    [InlineData("unknown-operation", "query=x")]
    [InlineData("search", "query=ok&..%2Fadmin=x")]
    [InlineData("search", "query=ok&%23fragment=1")]
    [InlineData("search", "query=ok&source=/api/config")]
    [InlineData("search", "query=")]
    [InlineData("releases", "indexers=Other")]
    [InlineData("releases", "indexers=")]
    [InlineData("releases", "expand_search=true")]
    [InlineData("releases", "expand_search=")]
    [InlineData("releases", "source=direct_download")]
    [InlineData("releases", "unknown=x")]
    public void BuildUpstreamPath_RejectsEverythingThatCouldLeaveTheWhitelist(string operation, string rawQuery)
    {
        var query = ParseRawQuery(rawQuery);
        Assert.Null(CreateService().BuildUpstreamPath(operation, query));
    }

    [Fact]
    public void BuildUpstreamPath_RejectsOversizedQueries()
    {
        var query = new Dictionary<string, string?> { ["query"] = new string('a', 201) };
        Assert.Null(CreateService().BuildUpstreamPath("search", query));
    }

    [Fact]
    public async Task GetAsync_SendsOnlyPluginDerivedIdentityAndNoInboundIdentity()
    {
        using var upstream = new UpstreamStub(
            """
            {"status": "ok"}
            """);

        var result = await CreateService().GetAsync(
            upstream.BaseUrl,
            "/api/status",
            remoteUser: "jellyfin_user",
            remoteGroups: new[] { "admins" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.False(result.TransportFailed);
        Assert.Equal("jellyfin_user", upstream.Headers["Remote-User"]);
        Assert.Equal("admins", upstream.Headers["Remote-Groups"]);

        // Identity values a malicious client could have sent must never pass through.
        Assert.False(upstream.Headers.ContainsKey("X-Auth-User"));
        Assert.False(upstream.Headers.ContainsKey("X-Auth-Groups"));
        Assert.False(upstream.Headers.ContainsKey("Cookie"));
        Assert.False(upstream.Headers.ContainsKey("Authorization"));
        Assert.False(upstream.Headers.ContainsKey("X-Emby-Token"));
        Assert.False(upstream.Headers.ContainsKey("X-Jellyfin-Token"));
        Assert.False(upstream.Headers.ContainsKey("Remote-Groups-Injected"));
        Assert.False(upstream.Headers.ContainsKey("Remote-User-Injected"));
    }

    private static IReadOnlyDictionary<string, string?> ParseRawQuery(string rawQuery)
    {
        var query = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var part in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
            query[key] = value;
        }

        return query;
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class UpstreamStub : IDisposable
    {
        private readonly HttpListener _listener;

        public UpstreamStub(string responseBody)
        {
            _listener = new HttpListener();
            Port = GetFreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            BaseUrl = $"http://127.0.0.1:{Port}";
            _ = Task.Run(() => Serve(responseBody));
        }

        public string BaseUrl { get; }

        public int Port { get; }

        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        private static int GetFreePort()
        {
            var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)socket.LocalEndPoint).Port;
            socket.Dispose();
            return port;
        }

        private async Task Serve(string responseBody)
        {
            var context = await _listener.GetContextAsync();
            foreach (var key in context.Request.Headers.AllKeys)
            {
                if (key != null)
                {
                    Headers[key] = context.Request.Headers[key]!;
                }
            }

            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(responseBody);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
