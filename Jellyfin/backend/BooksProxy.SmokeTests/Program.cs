using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Api;
using Moonfin.Server.Services;

static class Program
{
    private static int _checks;

    static async Task Main()
    {
        var logger = new CapturingLogger();
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var factory = new ClientFactory(client);
        var service = new ShelfmarkProxyService(factory, logger);
        var handles = new BooksReleaseHandles(new EphemeralDataProtectionProvider());
        var jobs = new BooksReleaseJobs();
        using (var configuredHandler = ShelfmarkProxyService.CreateHttpHandler())
        {
            Check(!configuredHandler.UseCookies && !configuredHandler.AllowAutoRedirect,
                "named client handler disables cookies and redirects");
        }

        Check(service.BuildUpstreamPath("search", new Dictionary<string, string?> { ["query"] = "Dune & Sea" })
            == "/api/metadata/search?query=Dune%20%26%20Sea", "metadata query is encoded");
        Check(service.BuildUpstreamPath("releases", new Dictionary<string, string?>
            { ["provider"] = "openlibrary", ["book_id"] = "42" })
            == "/api/releases?book_id=42&provider=openlibrary&source=prowlarr", "Prowlarr is selected");
        Check(service.BuildUpstreamPath("releases", new Dictionary<string, string?>
            { ["source"] = "direct_download" }) is null, "disabled source is rejected");
        Check(service.BuildUpstreamPath("releases", new Dictionary<string, string?>
            { ["source"] = "" }) is null, "empty source cannot fall back to all release sources");
        Check(service.BuildUpstreamPath("releases", new Dictionary<string, string?>
            { ["provider"] = "direct_download" }) is null, "disabled provider is rejected");
        Check(service.BuildUpstreamPath("releases", new Dictionary<string, string?>
            { ["source"] = "prowlarr" }) == "/api/releases?source=prowlarr", "only Prowlarr source is accepted");
        Check(service.BuildUpstreamPath("releases", new Dictionary<string, string?>
            { ["job_id"] = "opaque" }) is null, "job ID cannot be forwarded upstream");
        foreach (var forbidden in new[] { "indexers", "expand_search", "unexpected" })
        {
            Check(service.BuildUpstreamPath("releases", new Dictionary<string, string?>
                { [forbidden] = "Treasure Maps" }) is null, $"{forbidden} cannot override Shelfmark settings");
            Check(service.BuildUpstreamPath("releases", new Dictionary<string, string?>
                { [forbidden] = "" }) is null, $"empty {forbidden} is still rejected");
        }
        Check(service.BuildUpstreamPath("search", new Dictionary<string, string?>
            { ["query"] = "", ["on_behalf_of_user_id"] = "1" }) is null, "identity query is rejected");
        Check(service.BuildUpstreamPath("status", new Dictionary<string, string?>
            { ["user_id"] = "1" }) is null, "status cannot select another user");

        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json"),
        };
        var get = await service.GetAsync("http://shelfmark", "/api/status", "alice", [], CancellationToken.None);
        Check(get.IsSuccess && get.Body == "{\"status\":\"ok\"}", "GET returns JSON body");
        Check(factory.LastName == "MoonfinBooksShelfmark"
            && ShelfmarkProxyService.ShortRequestTimeout == TimeSpan.FromSeconds(30),
            "status uses the short-timeout client");
        Check(handler.LastRequest?.Headers.GetValues("Remote-User").Single() == "alice", "identity header sent");
        Check(!handler.LastRequest!.Headers.Contains("Remote-Groups"), "ordinary user has no admin group");
        Check(!handler.LastRequest.Headers.Contains("Cookie") && !handler.LastRequest.Headers.Contains("Authorization"),
            "client credentials are not forwarded");

        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"releases\":[]}", Encoding.UTF8, "application/json"),
        };
        var releases = await service.GetAsync("http://shelfmark", "/api/releases?source=prowlarr", "alice", [], CancellationToken.None);
        Check(releases.IsSuccess && factory.LastName == "MoonfinBooksShelfmarkReleases"
            && ShelfmarkProxyService.ReleaseSearchTimeout == TimeSpan.FromSeconds(210),
            "release search uses the long-timeout client");

        var rawSource = "https://indexer.invalid/grab?apikey=release-secret";
        var handle = handles.Issue("alice", rawSource);
        Check(!handle.Contains("release-secret") && handles.TryRead("alice", handle, out var decoded)
            && decoded == rawSource, "release source is protected and decodes for its owner");
        Check(!handles.TryRead("bob", handle, out _), "release handle is bound to one user");
        Check(!handles.TryRead("alice", "X" + handle[1..], out _), "tampered handle is rejected");
        Check(!handles.TryRead("alice", rawSource, out _), "raw source ID has no fallback");
        Check(!handles.TryRead("alice", handles.Issue("alice", rawSource, TimeSpan.FromSeconds(-1)), out _),
            "expired release handle is rejected");
        using (var bodyWithHandle = JsonDocument.Parse(JsonSerializer.Serialize(new
            { source = "prowlarr", source_id = handle, content_type = "ebook" })))
        {
            Check(handles.TryRead("alice", bodyWithHandle.RootElement.GetProperty("source_id").GetString()!, out var raw)
                && TryBuildDownloadWithSource(bodyWithHandle.RootElement, raw, out var upstreamBody)
                && upstreamBody.Contains(rawSource) && !upstreamBody.Contains(handle),
                "download resolves the handle before sending the source ID upstream");
        }

        var download = BuildDownload("""{"source":"prowlarr","source_id":"tm:42","title":"Dune","author":"Frank Herbert","year":1965,"language":"en","format":"txt","content_type":"ebook","extra":{"download_url":"https://evil.invalid/file"},"on_behalf_of_user_id":3,"download_url":"https://evil.invalid/file"}""");
        using (var parsed = JsonDocument.Parse(download))
        {
            var body = parsed.RootElement;
            Check(body.GetProperty("source").GetString() == "prowlarr"
                && body.GetProperty("author").GetString() == "Frank Herbert"
                && body.GetProperty("year").GetString() == "1965"
                && body.GetProperty("language").GetString() == "en"
                && body.GetProperty("format").GetString() == "txt", "validated metadata survives download serialization");
            Check(!body.TryGetProperty("extra", out _)
                && !body.TryGetProperty("on_behalf_of_user_id", out _)
                && !body.TryGetProperty("download_url", out _), "untrusted extra and URLs are dropped");
        }
        foreach (var format in new[] { "epub", "mobi", "azw3", "pdf", "fb2", "djvu", "cbz", "cbr", "txt", "rtf", "doc", "docx", "zip", "rar" })
        {
            BuildDownload(JsonSerializer.Serialize(new { source = "prowlarr", source_id = "tm:42", format, content_type = "ebook" }));
        }
        foreach (var format in new[] { "m4b", "mp3", "m4a", "mp4", "flac", "ogg", "wma", "aac", "wav", "opus", "zip", "rar" })
        {
            BuildDownload(JsonSerializer.Serialize(new { source = "prowlarr", source_id = "tm:42", format, content_type = "audiobook" }));
        }
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","format":"exe","content_type":"ebook"}"""), "unknown format is rejected");
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","format":"mp3","content_type":"ebook"}"""), "audiobook format cannot masquerade as ebook");
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","format":"epub","content_type":"audiobook"}"""), "ebook format cannot masquerade as audiobook");
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","content_type":"other"}"""), "unknown content type is rejected");
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","year":"2024/evil"}"""), "invalid year is rejected");
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","year":true}"""), "non-year value is rejected");
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","language":"../../x"}"""), "invalid language is rejected");
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","language":42}"""), "non-string language is rejected");
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","author":"Bad\nName"}"""), "control characters in author are rejected");
        Check(!TryBuildDownload("""{"source":"prowlarr","source_id":"x","author":42}"""), "non-string author is rejected");
        BuildDownload(JsonSerializer.Serialize(new { source = "prowlarr", source_id = new string('x', 69) }));
        Check(!TryBuildDownload(JsonSerializer.Serialize(new { source = "prowlarr", source_id = new string('x', 2049) })),
            "oversized source_id is rejected");
        Check(!TryBuildDownload("""{"source":"direct_download","source_id":"x"}"""), "disabled download rejected");
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"queued\"}", Encoding.UTF8, "application/json"),
        };
        var posted = await service.PostJsonAsync("http://shelfmark", "alice", [], download, CancellationToken.None);
        Check(posted.IsSuccess && handler.LastBody == download, "mock download submits sanitized JSON");
        Check(factory.LastName == "MoonfinBooksShelfmark", "download queue uses the short-timeout client");

        var rawTask = "https://indexer.invalid/task?apikey=task-secret";
        var global = new ShelfmarkProxyResult
        {
            StatusCode = HttpStatusCode.OK,
            Body = JsonSerializer.Serialize(new { queued = new Dictionary<string, object>
            {
                [rawTask] = new { username = "alice", title = "A", progress = 12, format = "epub", content_type = "ebook", download_path = "/private/task-secret" },
                ["foreign"] = new { username = "bob", title = "B" },
                ["unknown"] = new { title = "Old" },
            }, downloading = new { } }),
        };
        var own = ShelfmarkProxyService.FilterStatusForUser(global, "alice");
        var displayId = ShelfmarkProxyService.DisplayTaskId("alice", rawTask);
        Check(own.IsSuccess && own.Body.Contains(displayId) && !own.Body.Contains("task-secret")
            && !own.Body.Contains("download_path") && !own.Body.Contains("foreign")
            && !own.Body.Contains("unknown"), "owned status exposes only safe fields and digest IDs");
        Check(ShelfmarkProxyService.DisplayTaskId("alice", rawTask) != ShelfmarkProxyService.DisplayTaskId("bob", rawTask),
            "task display IDs are user-bound");
        var active = ShelfmarkProxyService.FilterActiveForUser(new ShelfmarkProxyResult
        {
            StatusCode = HttpStatusCode.OK,
            Body = JsonSerializer.Serialize(new { active_downloads = new[] { rawTask, "foreign" } }),
        }, global, "alice");
        Check(active.IsSuccess && active.Body == JsonSerializer.Serialize(new { active_downloads = new[] { displayId } }),
            "active IDs match projected status and stay private");

        var controller = new BooksProxyController(service, handles, jobs);
        var pending = (ObjectResult)typeof(BooksProxyController)
            .GetMethod("Pending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, ["opaque-job"])!;
        Check(pending.StatusCode == 202
            && JsonSerializer.Serialize(pending.Value).Contains("\"retry_after\":2")
            && JsonSerializer.Serialize(pending.Value).Contains("\"job_id\":\"opaque-job\""),
            "release pending reply has the polling contract");
        var projectReleases = typeof(BooksProxyController).GetMethod("ProjectReleases", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var projected = (ShelfmarkProxyResult)projectReleases.Invoke(controller, [new ShelfmarkProxyResult
        {
            StatusCode = HttpStatusCode.OK,
            Body = JsonSerializer.Serialize(new { releases = new[] { new
            {
                source = "prowlarr", source_id = rawSource, title = "A", format = "epub", language = "en", size = "1 MB",
                info_url = rawSource, download_url = rawSource, extra = new { api_key = "release-secret" },
            } } }),
        }, "alice"])!;
        Check(projected.IsSuccess && !projected.Body.Contains("release-secret") && !projected.Body.Contains("info_url")
            && !projected.Body.Contains("extra") && !projected.Body.Contains("download_url"),
            "release projection strips URL and extra fields, including raw source ID");
        using (var projectedJson = JsonDocument.Parse(projected.Body))
        {
            var release = projectedJson.RootElement.GetProperty("releases")[0];
            Check(release.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(
                new[] { "format", "language", "size", "source", "source_id", "title" }),
                "release projection has exactly the UI fields");
            Check(handles.TryRead("alice", release.GetProperty("source_id").GetString()!, out var original)
                && original == rawSource, "projected release handle decodes for download");
        }
        var projectDownload = typeof(BooksProxyController).GetMethod("ProjectDownload", BindingFlags.Static | BindingFlags.NonPublic)!;
        var projectedDownload = (ShelfmarkProxyResult)projectDownload.Invoke(null, [new ShelfmarkProxyResult
        {
            StatusCode = HttpStatusCode.OK,
            Body = """{"status":"queued","task_id":"https://indexer.invalid/?apikey=download-secret"}""",
        }])!;
        Check(projectedDownload.Body == "{\"status\":\"queued\"}", "download success exposes only status");

        var wait = new TaskCompletionSource<ShelfmarkProxyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new BooksReleaseJobs();
        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            Check(queue.TryStart("alice", _ => wait.Task, out var id), "global release capacity accepts four jobs");
            ids.Add(id);
        }
        Check(!queue.TryStart("alice", _ => wait.Task, out _), "fifth concurrent release is rejected");
        Check(queue.Find("bob", ids[0]) is null && queue.Find("alice", "missing") is null,
            "jobs cannot be polled by another user or guessed");
        Check(queue.Find("alice", ids[0]) is { IsCompleted: false }, "owner can poll pending job");
        wait.SetResult(new ShelfmarkProxyResult { StatusCode = HttpStatusCode.OK, Body = "{\"releases\":[]}" });
        Check((await queue.Find("alice", ids[0])!).IsSuccess, "owner can poll completed job");
        var fakeClock = new AdjustableClock();
        var retained = new BooksReleaseJobs(fakeClock);
        string? firstRetainedId = null;
        for (var i = 0; i < 32; i++)
        {
            Check(retained.TryStart("alice", _ => Task.FromResult(new ShelfmarkProxyResult
                { StatusCode = HttpStatusCode.OK, Body = "{\"releases\":[]}" }), out var retainedId),
                "retained job fits bounded store");
            firstRetainedId ??= retainedId;
            await retained.Find("alice", retainedId)!;
        }
        Check(!retained.TryStart("alice", _ => wait.Task, out _), "retained job limit is enforced");
        fakeClock.Advance(TimeSpan.FromMinutes(6));
        Check(retained.Find("alice", firstRetainedId!) is null, "expired job cannot be polled");
        Check(retained.TryStart("alice", _ => wait.Task, out _), "expired jobs are evicted");
        controller.ControllerContext.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("Jellyfin-UserId", Guid.Empty.ToString())], "API-key")),
        };
        var emptyIdentity = await controller.GetStatus();
        Check(emptyIdentity is UnauthorizedObjectResult, "Guid.Empty API-key identity is rejected");
        Check(typeof(BooksProxyController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Length == 1,
            "all Books routes require Jellyfin authorization");
        var toResult = typeof(BooksProxyController).GetMethod("ToActionResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var jsonResult = (ContentResult)toResult.Invoke(controller, [get])!;
        Check(jsonResult.ContentType!.StartsWith("application/json") && jsonResult.Content == get.Body,
            "JSON is returned as JSON, not a serialized string");
        var nonObject = (ObjectResult)toResult.Invoke(controller, [new ShelfmarkProxyResult
        {
            StatusCode = HttpStatusCode.OK,
            Body = "[1,2]",
        }])!;
        Check(nonObject.StatusCode == 502, "non-object upstream JSON is rejected");

        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', ShelfmarkProxyResult.MaxResponseBytes + 1)),
        };
        var oversized = await service.GetAsync("http://shelfmark", "/api/status", "alice", [], CancellationToken.None);
        Check(oversized.BodyTruncated && oversized.Body.Length == 0, "oversized response is discarded");
        var oversizedResult = (ObjectResult)toResult.Invoke(controller, [oversized])!;
        Check(oversizedResult.StatusCode == 502, "oversized response becomes gateway error");

        handler.NextException = new HttpRequestException("secret-key?query=sensitive");
        var failed = await service.GetAsync("http://shelfmark", "/api/status", "alice", [], CancellationToken.None);
        Check(failed.TransportFailed && !logger.Messages.Any(x => x.Contains("secret-key")),
            "exception and query are not logged");

        Console.WriteLine($"Books proxy smoke tests passed: {_checks} checks");
    }

    private static string BuildDownload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var args = new object?[] { doc.RootElement, doc.RootElement.GetProperty("source_id").GetString(), null, null };
        var method = typeof(BooksProxyController).GetMethod("TryBuildDownloadPayload", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((bool)method.Invoke(null, args)!, "download payload accepted");
        return (string)args[2]!;
    }

    private static bool TryBuildDownload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var args = new object?[] { doc.RootElement, doc.RootElement.GetProperty("source_id").GetString(), null, null };
        return (bool)typeof(BooksProxyController)
            .GetMethod("TryBuildDownloadPayload", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args)!;
    }

    private static bool TryBuildDownloadWithSource(JsonElement root, string sourceId, out string body)
    {
        var args = new object?[] { root, sourceId, null, null };
        var accepted = (bool)typeof(BooksProxyController)
            .GetMethod("TryBuildDownloadPayload", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args)!;
        body = (string?)args[2] ?? string.Empty;
        return accepted;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        _checks++;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public string? LastName { get; private set; }
        public HttpClient CreateClient(string name)
        {
            Check(name is "MoonfinBooksShelfmark" or "MoonfinBooksShelfmarkReleases", "named Shelfmark client used");
            LastName = name;
            return client;
        }
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpResponseMessage? NextResponse { get; set; }
        public Exception? NextException { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (NextException is { } error)
            {
                NextException = null;
                throw error;
            }

            var response = NextResponse ?? throw new Exception("Missing mock response");
            NextResponse = null;
            return response;
        }
    }

    private sealed class CapturingLogger : ILogger<ShelfmarkProxyService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

namespace Moonfin.Server
{
    public sealed class MoonfinPlugin
    {
        public static MoonfinPlugin? Instance { get; set; }
        public BooksConfiguration Configuration { get; } = new();
    }

    public sealed class BooksConfiguration
    {
        public bool BooksEnabled { get; set; }
        public string? GetEffectiveShelfmarkUrl() => "http://shelfmark";
    }
}

namespace Moonfin.Server.Api
{
    public static class ControllerExtensions
    {
        public static Guid? GetUserIdFromClaims(this ControllerBase controller) =>
            Guid.TryParse(controller.User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : null;
        public static bool IsAdminFromClaims(this ControllerBase controller) => false;
    }
}
