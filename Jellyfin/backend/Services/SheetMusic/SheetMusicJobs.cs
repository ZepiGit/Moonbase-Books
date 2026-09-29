using System.Text.Json;

namespace Moonfin.Server.Services;

public sealed record SheetMusicJob(
    string Id,
    string UserId,
    string PieceId,
    string Title,
    string Composer,
    string License,
    string SourceUrl,
    string PdfUrl,
    string Status,
    string? Error = null,
    string? FilePath = null,
    string? ItemId = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>Small durable queue shared with the host-side score importer.</summary>
public sealed class SheetMusicJobs
{
    private readonly string _directory;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SheetMusicJobs(string directory) => _directory = directory;

    public SheetMusicJob? Find(string owner, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        try
        {
            var path = Path.Combine(_directory, id + ".json");
            if (!File.Exists(path)) return null;
            var job = JsonSerializer.Deserialize<SheetMusicJob>(File.ReadAllText(path), Json);
            return job?.UserId == owner && job.Id == id ? job : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public SheetMusicJob? Enqueue(string owner, SheetMusicPiece piece)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var jobs = Directory.EnumerateFiles(_directory, "*.json")
                .Select(path =>
                {
                    try { return JsonSerializer.Deserialize<SheetMusicJob>(File.ReadAllText(path), Json); }
                    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                    { return null; }
                })
                .Where(job => job != null)
                .ToArray();
            var existing = jobs.FirstOrDefault(job => job!.UserId == owner && job.PieceId == piece.Id
                && (job.Status is "queued" or "fetching" or "importing" or "ready"));
            if (existing != null) return existing;
            if (jobs.Count(job => job!.Status is "queued" or "fetching" or "importing") >= 4) return null;
            if (jobs.Count(job => job!.UserId == owner
                && (job.Status is "queued" or "fetching" or "importing")) >= 2) return null;

            var id = Guid.NewGuid().ToString("N");
            var job = new SheetMusicJob(id, owner, piece.Id, piece.Title, piece.Composer,
                piece.License, piece.SourceUrl, piece.PdfUrl, "queued",
                CreatedAt: DateTimeOffset.UtcNow);
            var path = Path.Combine(_directory, id + ".json");
            var temporary = Path.Combine(_directory, "." + id + ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(job, Json));
            File.Move(temporary, path);
            return job;
        }
    }
}
