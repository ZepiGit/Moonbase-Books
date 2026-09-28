using System.Net;
using System.Net.Mime;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Authenticated proxy between Moonfin clients and Shelfmark. Jellyfin authenticates the
/// caller with its normal token auth; the controller resolves the user's name and admin
/// role from that identity and signs each upstream call itself. Client-supplied identity
/// headers and cookies are never forwarded and the Jellyfin token never leaves Jellyfin.
/// </summary>
[ApiController]
[Route("Moonfin/Books/v1")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class BooksProxyController : ControllerBase
{
    private const int MaxDownloadBodyBytes = 32 * 1024;
    // This Books API currently supports the Shelfmark Prowlarr adapter. Other release
    // sources are disabled there and must not be exposed through this API.
    private const string AllowedDownloadSource = "prowlarr";
    private static readonly HashSet<string> EbookFormats = new(StringComparer.Ordinal)
    {
        "epub", "mobi", "azw3", "pdf", "fb2", "djvu", "cbz", "cbr",
        "txt", "rtf", "doc", "docx", "zip", "rar",
    };
    private static readonly HashSet<string> AudiobookFormats = new(StringComparer.Ordinal)
    {
        "m4b", "mp3", "m4a", "mp4", "flac", "ogg", "wma", "aac",
        "wav", "opus", "zip", "rar",
    };

    private readonly ShelfmarkProxyService _proxy;
    private readonly BooksReleaseHandles _handles;
    private readonly BooksReleaseJobs _jobs;

    public BooksProxyController(ShelfmarkProxyService proxy, BooksReleaseHandles handles, BooksReleaseJobs jobs)
    {
        _proxy = proxy;
        _handles = handles;
        _jobs = jobs;
    }

    [HttpGet("Status")]
    public Task<IActionResult> GetStatus() => ForwardGet("status");

    [HttpGet("Search")]
    public Task<IActionResult> GetSearch() => ForwardGet("search");

    [HttpGet("Releases")]
    public Task<IActionResult> GetReleases() => ForwardGet("releases");

    [HttpGet("Active")]
    public Task<IActionResult> GetActive() => ForwardGet("active");

    [HttpPost("Download")]
    [Consumes(MediaTypeNames.Application.Json)]
    public Task<IActionResult> PostDownload() => ForwardDownload();

    private async Task<IActionResult> ForwardGet(string operation)
    {
        if (!TryResolveCaller(out var caller))
        {
            return ProblemForCaller();
        }

        var query = Request.Query.ToDictionary(
            static kv => kv.Key,
            static kv => (string?)kv.Value.ToString());
        var baseUrl = ResolveBaseUrl();
        if (baseUrl == null)
        {
            return BooksUnavailable();
        }

        if (operation == "releases" && query.TryGetValue("job_id", out var jobId))
        {
            if (query.Count != 1 || jobId is null || !Regex.IsMatch(jobId, "\\A[0-9a-f]{32}\\z"))
            {
                return BadRequest(new { error = "Invalid release job" });
            }

            var job = _jobs.Find(caller.User, jobId);
            if (job == null)
            {
                return NotFound(new { error = "Release job not found" });
            }

            return job.IsCompleted ? ToActionResult(await job.ConfigureAwait(false)) : Pending(jobId);
        }

        var upstreamPath = _proxy.BuildUpstreamPath(operation, query);
        if (upstreamPath == null)
        {
            return BadRequest(new { error = "Invalid books request parameters" });
        }

        if (operation == "releases")
        {
            if (!_jobs.TryStart(caller.User, async cancellationToken =>
                ProjectReleases(await _proxy.GetAsync(baseUrl, upstreamPath, caller.User, caller.Groups,
                    cancellationToken).ConfigureAwait(false), caller.User), out var startedId))
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many release jobs" });
            }

            return Pending(startedId);
        }

        var result = await _proxy.GetAsync(
            baseUrl, upstreamPath, caller.User, caller.Groups, HttpContext.RequestAborted).ConfigureAwait(false);
        if (operation == "status" && result.IsSuccess)
        {
            result = ShelfmarkProxyService.FilterStatusForUser(result, caller.User);
        }
        else if (operation == "active" && result.IsSuccess)
        {
            var status = await _proxy.GetAsync(baseUrl, "/api/status", caller.User, caller.Groups,
                HttpContext.RequestAborted).ConfigureAwait(false);
            if (!status.IsSuccess)
            {
                return ToActionResult(status);
            }

            result = ShelfmarkProxyService.FilterActiveForUser(result, status, caller.User);
        }

        return ToActionResult(result);
    }

    private IActionResult Pending(string jobId) =>
        StatusCode(StatusCodes.Status202Accepted, new { status = "pending", job_id = jobId, retry_after = 2 });

    private async Task<IActionResult> ForwardDownload()
    {
        if (!TryResolveCaller(out var caller))
        {
            return ProblemForCaller();
        }

        var baseUrl = ResolveBaseUrl();
        if (baseUrl == null)
        {
            return BooksUnavailable();
        }

        var read = await Request.Body.ReadAtMostAsync(MaxDownloadBodyBytes, HttpContext.RequestAborted).ConfigureAwait(false);
        if (read.Length > MaxDownloadBodyBytes)
        {
            return BadRequest(new { error = "Download request body too large" });
        }

        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(read.ToArray());
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON body" });
        }

        using (payload)
        {
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BadRequest(new { error = "Invalid download request" });
            }

            var sourceHandle = GetTrimmedString(payload.RootElement, "source_id");
            if (sourceHandle is null || !_handles.TryRead(caller.User, sourceHandle, out var sourceId))
            {
                return BadRequest(new { error = "Invalid release handle" });
            }

            if (!TryBuildDownloadPayload(payload.RootElement, sourceId, out var jsonBody, out var error))
            {
                return BadRequest(new { error });
            }

            var result = await _proxy.PostJsonAsync(
                baseUrl, caller.User, caller.Groups, jsonBody, HttpContext.RequestAborted).ConfigureAwait(false);
            return ToActionResult(ProjectDownload(result));
        }
    }

    private ShelfmarkProxyResult ProjectReleases(ShelfmarkProxyResult response, string username)
    {
        if (!response.IsSuccess)
        {
            return response;
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("releases", out var releases)
                || releases.ValueKind != JsonValueKind.Array)
            {
                return InvalidResponse();
            }

            var projected = new List<Dictionary<string, object?>>();
            foreach (var release in releases.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || !release.TryGetProperty("source", out var source)
                    || source.ValueKind != JsonValueKind.String
                    || source.GetString() != AllowedDownloadSource
                    || !release.TryGetProperty("source_id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { Length: > 0 and <= 2048 } rawId
                    || rawId.Any(char.IsControl))
                {
                    return InvalidResponse();
                }

                var item = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["source"] = AllowedDownloadSource,
                    ["source_id"] = _handles.Issue(username, rawId),
                };
                foreach (var field in new[] { "title", "format", "language", "size" })
                {
                    if (release.TryGetProperty(field, out var value))
                    {
                        item[field] = value.Clone();
                    }
                }

                projected.Add(item);
            }

            return new ShelfmarkProxyResult { StatusCode = response.StatusCode,
                Body = JsonSerializer.Serialize(new { releases = projected }) };
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
    }

    private static ShelfmarkProxyResult ProjectDownload(ShelfmarkProxyResult response)
    {
        if (!response.IsSuccess)
        {
            return response;
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() == "queued")
            {
                return new ShelfmarkProxyResult { StatusCode = response.StatusCode,
                    Body = JsonSerializer.Serialize(new { status = status.GetString() }) };
            }
        }
        catch (JsonException)
        {
            // Malformed upstream success is a gateway error.
        }

        return InvalidResponse();
    }

    private static ShelfmarkProxyResult InvalidResponse() => new()
    {
        StatusCode = HttpStatusCode.BadGateway,
        TransportFailed = true,
        TransportError = "Invalid Shelfmark response",
    };

    private bool TryResolveCaller(out (string User, List<string> Groups) caller)
    {
        caller = (string.Empty, new List<string>());

        var userId = this.GetUserIdFromClaims();
        if (userId == null || userId == Guid.Empty)
        {
            return false;
        }

        var username = ResolveUsername(userId.Value);
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl))
        {
            return false;
        }

        caller.User = username;
        if (this.IsAdminFromClaims())
        {
            caller.Groups.Add("admins");
        }

        return true;
    }

    private IActionResult ProblemForCaller() =>
        Unauthorized(new { error = "User identity could not be resolved" });

    private string? ResolveUsername(Guid userId)
    {
        // IUserManager lives in the host assembly that is referenced compile-only, so the
        // same reflection bind the other controllers use is the cheapest safe lookup here.
        var userManagerType = Type.GetType(
            "MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");
        var getUserById = userManagerType?.GetMethod("GetUserById", [typeof(Guid)]);
        if (getUserById == null)
        {
            return null;
        }

        var userManager = HttpContext.RequestServices.GetService(userManagerType!);
        if (userManager == null)
        {
            return null;
        }

        var user = getUserById.Invoke(userManager, [userId]);
        var username = user?.GetType().GetProperty("Username")?.GetValue(user) as string;
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        return username;
    }

    private string? ResolveBaseUrl() =>
        MoonfinPlugin.Instance?.Configuration is { BooksEnabled: true } config
            ? config.GetEffectiveShelfmarkUrl()
            : null;

    private IActionResult BooksUnavailable() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Books integration is not enabled" });

    private IActionResult ToActionResult(ShelfmarkProxyResult result)
    {
        if (result.TransportFailed)
        {
            return StatusCode((int)result.StatusCode, new { error = result.TransportError ?? "Upstream request failed" });
        }

        if (result.BodyTruncated)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Shelfmark response too large" });
        }

        if (!result.IsSuccess)
        {
            var status = (int)result.StatusCode;
            return StatusCode(status is >= 300 and < 400 ? StatusCodes.Status502BadGateway : status,
                new { error = "Shelfmark request failed" });
        }

        try
        {
            using var parsed = JsonDocument.Parse(result.Body);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Invalid Shelfmark response" });
            }
        }
        catch (JsonException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Invalid Shelfmark response" });
        }

        return new ContentResult
        {
            Content = result.Body,
            ContentType = "application/json; charset=utf-8",
            StatusCode = (int)result.StatusCode,
        };
    }

    private static bool TryBuildDownloadPayload(JsonElement root, string sourceId, out string jsonBody, out string? error)
    {
        jsonBody = string.Empty;
        error = null;

        var source = GetTrimmedString(root, "source");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(sourceId))
        {
            error = "source and source_id are required";
            return false;
        }

        if (!string.Equals(source, AllowedDownloadSource, StringComparison.Ordinal))
        {
            error = "Unsupported download source";
            return false;
        }

        if (sourceId.Length > 2048 || sourceId.Any(char.IsControl))
        {
            error = "source_id is invalid";
            return false;
        }

        var contentType = GetTrimmedString(root, "content_type");
        if (root.TryGetProperty("content_type", out var contentTypeElement)
            && contentTypeElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            error = "Unsupported content type";
            return false;
        }

        if (!string.IsNullOrEmpty(contentType) && contentType is not ("ebook" or "audiobook"))
        {
            error = "Unsupported content type";
            return false;
        }

        var format = GetTrimmedString(root, "format")?.ToLowerInvariant();
        if (root.TryGetProperty("format", out var formatElement)
            && formatElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            error = "Unsupported download format";
            return false;
        }

        if (!string.IsNullOrEmpty(format)
            && !(contentType == "ebook" ? EbookFormats.Contains(format)
                : contentType == "audiobook" ? AudiobookFormats.Contains(format)
                : EbookFormats.Contains(format) || AudiobookFormats.Contains(format)))
        {
            error = "Unsupported download format";
            return false;
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = source,
            ["source_id"] = sourceId,
        };

        var title = GetTrimmedString(root, "title");
        if (!string.IsNullOrEmpty(title))
        {
            if (title.Length > 300 || title.Any(char.IsControl))
            {
                error = "title is invalid";
                return false;
            }

            payload["title"] = title;
        }

        if (!string.IsNullOrEmpty(format))
        {
            payload["format"] = format;
        }

        foreach (var (field, maxLength) in new[]
        {
            ("author", 300), ("series_name", 300), ("subtitle", 300),
        })
        {
            if (root.TryGetProperty(field, out var element)
                && element.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                error = $"{field} is invalid";
                return false;
            }

            var value = GetTrimmedString(root, field);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (value.Length > maxLength || value.Any(char.IsControl))
            {
                error = $"{field} is invalid";
                return false;
            }

            payload[field] = value;
        }

        if (root.TryGetProperty("year", out var yearElement)
            && yearElement.ValueKind != JsonValueKind.Null)
        {
            var year = yearElement.ValueKind switch
            {
                JsonValueKind.String => yearElement.GetString()?.Trim(),
                JsonValueKind.Number when yearElement.TryGetInt32(out var value) =>
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
            if (year is not { Length: 4 } || !year.All(static digit => digit is >= '0' and <= '9'))
            {
                error = "year is invalid";
                return false;
            }

            payload["year"] = year;
        }

        if (root.TryGetProperty("language", out var languageElement)
            && languageElement.ValueKind != JsonValueKind.Null)
        {
            var language = languageElement.ValueKind == JsonValueKind.String
                ? languageElement.GetString()?.Trim()
                : null;
            if (language is null || !Regex.IsMatch(language, @"\A[a-zA-Z]{2,3}(?:-[a-zA-Z]{2})?\z"))
            {
                error = "language is invalid";
                return false;
            }

            payload["language"] = language;
        }

        if (root.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.String)
        {
            var size = sizeElement.GetString()?.Trim();
            if (!string.IsNullOrEmpty(size))
            {
                if (size!.Length > 64)
                {
                    error = "size is too long";
                    return false;
                }

                payload["size"] = size;
            }
        }

        if (root.TryGetProperty("priority", out var priorityElement))
        {
            int? priority = priorityElement.ValueKind switch
            {
                JsonValueKind.Number when priorityElement.TryGetInt32(out var value) => value,
                JsonValueKind.String when int.TryParse(priorityElement.GetString(), out var value) => value,
                _ => null,
            };
            if (priority.HasValue && priority.Value is >= 0 and <= 9)
            {
                payload["priority"] = priority.Value;
            }
        }

        if (!string.IsNullOrEmpty(contentType))
        {
            payload["content_type"] = contentType;
        }

        jsonBody = JsonSerializer.Serialize(payload);
        return true;
    }

    private static string? GetTrimmedString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString()?.Trim();
    }
}

internal static class BooksRequestBodyReader
{
    public static async Task<ReadOnlyMemory<byte>> ReadAtMostAsync(
        this Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return buffer.AsMemory(0, total);
    }
}
