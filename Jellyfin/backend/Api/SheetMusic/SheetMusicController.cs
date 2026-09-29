using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Authenticated sheet-music search, request status and owner-only file access.
/// Sources are resolved server-side; clients cannot supply a download URL.
/// </summary>
[ApiController]
[Route("Moonfin/Books/v1/SheetMusic")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public sealed class SheetMusicController : ControllerBase
{
    private const int MaxResults = 100;
    private const int MaxQueryLength = 200;

    private readonly SheetMusicCatalogService _catalog;
    private readonly InternetArchiveScoreService _archive;
    private readonly SheetMusicJobs _jobs;

    public SheetMusicController(SheetMusicCatalogService catalog, InternetArchiveScoreService archive, SheetMusicJobs jobs)
    {
        _catalog = catalog;
        _archive = archive;
        _jobs = jobs;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? query, [FromQuery] string? composer,
        [FromQuery] string? instrument)
    {
        if (this.GetUserIdFromClaims() is not { } userId || userId == Guid.Empty)
        {
            return Unauthorized(new { error = "Invalid Jellyfin user" });
        }

        if (query is { Length: > MaxQueryLength } ||
            composer is { Length: > 100 } ||
            instrument is { Length: > 100 })
        {
            return BadRequest(new { error = "Invalid sheet music search parameters" });
        }

        IReadOnlyList<SheetMusicPiece> archive = [];
        var archiveFailed = false;
        if (!string.IsNullOrWhiteSpace(query) || !string.IsNullOrWhiteSpace(composer))
        {
            try
            {
                archive = await _archive.SearchAsync(query, composer, HttpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                archiveFailed = true;
            }
        }

        var pieces = _catalog.Pieces;
        var results = new List<object>();
        var truncated = archive.Count == 40;
        var mutopiaLimit = MaxResults - archive.Count;
        foreach (var piece in pieces)
        {
            if (!Matches(piece.Title, query) ||
                !Matches(piece.Composer, composer) ||
                !Matches(piece.Instrument, instrument))
            {
                continue;
            }

            // Snake-case keys match the client contract in
            // sheet_music_repository.dart exactly (the Books proxy endpoints use
            // the same wire style).
            if (results.Count == mutopiaLimit)
            {
                truncated = true;
                break;
            }

            results.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = "mutopia:" + piece.Id,
                ["source"] = "mutopia",
                ["title"] = piece.Title,
                ["composer"] = piece.Composer,
                ["opus"] = piece.Opus,
                ["instrument"] = piece.Instrument,
                ["style"] = piece.Style,
                ["license"] = piece.License,
                ["source_url"] = piece.SourceUrl,
                ["pdf_url"] = piece.PdfUrl,
            });
        }

        foreach (var piece in archive)
        {
            if (results.Count == MaxResults) { truncated = true; break; }
            results.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = piece.Id,
                ["source"] = "internet_archive",
                ["title"] = piece.Title,
                ["composer"] = piece.Composer,
                ["instrument"] = piece.Instrument,
                ["license"] = piece.License,
                ["source_url"] = piece.SourceUrl,
                ["pdf_url"] = piece.PdfUrl,
            });
        }

        if (archiveFailed && results.Count == 0)
            return StatusCode(502, new { error = "Score catalog search failed" });

        return Ok(new { pieces = results, count = results.Count, truncated });
    }

    public sealed record RequestBody(string Id);

    [HttpPost("Request")]
    public async Task<IActionResult> RequestScore([FromBody] RequestBody? body)
    {
        if (this.GetUserIdFromClaims() is not { } userId || userId == Guid.Empty)
            return Unauthorized(new { error = "Invalid Jellyfin user" });
        if (body?.Id is not { Length: > 0 and <= 160 } id)
            return BadRequest(new { error = "Invalid score selection" });

        SheetMusicPiece? piece;
        if (id.StartsWith("mutopia:", StringComparison.Ordinal))
        {
            piece = _catalog.Pieces.FirstOrDefault(item => item.Id == id[8..]);
            if (piece != null) piece = piece with { Id = id };
        }
        else if (id.StartsWith("ia:", StringComparison.Ordinal))
        {
            try { piece = await _archive.ResolveAsync(id[3..], HttpContext.RequestAborted).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            { return StatusCode(502, new { error = "Score source unavailable" }); }
        }
        else return BadRequest(new { error = "Unknown score source" });

        if (piece == null) return NotFound(new { error = "Score not found" });
        var job = _jobs.Enqueue(userId.ToString("N"), piece);
        if (job == null) return StatusCode(429, new { error = "Too many score requests" });
        return Accepted(new { job_id = job.Id, status = job.Status });
    }

    [HttpGet("Status")]
    public IActionResult GetStatus([FromQuery] string jobId)
    {
        if (this.GetUserIdFromClaims() is not { } userId || userId == Guid.Empty)
            return Unauthorized(new { error = "Invalid Jellyfin user" });
        var job = _jobs.Find(userId.ToString("N"), jobId);
        return job == null ? NotFound(new { error = "Score request not found" })
            : Ok(new { status = job.Status, title = job.Title, error = job.Error,
                item_id = job.Status == "ready" ? job.ItemId : null });
    }

    [HttpGet("File")]
    public IActionResult GetFile([FromQuery] string jobId)
    {
        if (this.GetUserIdFromClaims() is not { } userId || userId == Guid.Empty)
            return Unauthorized(new { error = "Invalid Jellyfin user" });
        var job = _jobs.Find(userId.ToString("N"), jobId);
        if (job is not { Status: "ready", FilePath: { } filePath })
            return NotFound(new { error = "Score file not ready" });
        var root = Path.GetFullPath("/media/sheetmusic") + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(filePath);
        if (!resolved.StartsWith(root, StringComparison.Ordinal) || !System.IO.File.Exists(resolved))
            return NotFound(new { error = "Score file not available" });
        var file = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(file, "application/pdf", Path.GetFileName(resolved));
    }

    private static bool Matches(string? value, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return value?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) == true;
    }
}
