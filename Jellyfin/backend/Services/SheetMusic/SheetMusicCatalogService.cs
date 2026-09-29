using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Serves a locally curated sheet-music catalog (one JSON file, produced offline from a
/// pinned MutopiaProject/MutopiaProject checkout by scripts/sheetmusic/build_sheetmusic_catalog.py).
/// Each entry carries provenance and the per-piece license exactly as Mutopia publishes
/// it; the service performs free-text and field filtering only and never exposes file
/// paths or download endpoints.
/// </summary>
public sealed class SheetMusicCatalogService
{
    private const int MaxCatalogEntries = 50_000;
    private const int MaxStringLength = 300;
    private const long MaxCatalogBytes = 32 * 1024 * 1024;

    private readonly object _loadLock = new();
    private readonly ILogger<SheetMusicCatalogService> _logger;
    private readonly string _catalogPath;
    private volatile IReadOnlyList<SheetMusicPiece>? _pieces;

    public SheetMusicCatalogService(string catalogPath, ILogger<SheetMusicCatalogService> logger)
    {
        _catalogPath = catalogPath;
        _logger = logger;
    }

    /// <summary>Loads the catalog lazily and reloads it when the file changes on disk.</summary>
    public IReadOnlyList<SheetMusicPiece> Pieces
    {
        get
        {
            lock (_loadLock)
            {
                if (_pieces is null || File.GetLastWriteTimeUtc(_catalogPath) != _loadedStamp)
                {
                    _pieces = Load();
                }

                return _pieces;
            }
        }
    }

    private DateTime _loadedStamp;

    private IReadOnlyList<SheetMusicPiece> Load()
    {
        try
        {
            if (new FileInfo(_catalogPath).Length > MaxCatalogBytes)
            {
                _logger.LogError("Sheet music catalog exceeds {Max} bytes", MaxCatalogBytes);
                _loadedStamp = File.GetLastWriteTimeUtc(_catalogPath);
                return Array.Empty<SheetMusicPiece>();
            }
            using var stream = File.OpenRead(_catalogPath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 8,
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaxCatalogEntries)
            {
                _logger.LogError("Sheet music catalog rejected: root must be an array of at most {Max} entries", MaxCatalogEntries);
                return Array.Empty<SheetMusicPiece>();
            }

            var pieces = new List<SheetMusicPiece>(root.GetArrayLength());
            foreach (var entry in root.EnumerateArray())
            {
                var piece = ParsePiece(entry);
                if (piece is null)
                {
                    _logger.LogWarning("Sheet music catalog entry skipped: missing required fields");
                    continue;
                }

                pieces.Add(piece);
            }

            _loadedStamp = File.GetLastWriteTimeUtc(_catalogPath);
            return pieces;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or JsonException)
        {
            _loadedStamp = File.GetLastWriteTimeUtc(_catalogPath);
            if (ex is not FileNotFoundException)
            {
                _logger.LogError(ex, "Sheet music catalog could not be read from {Path}", _catalogPath);
            }
            return Array.Empty<SheetMusicPiece>();
        }
    }

    private static SheetMusicPiece? ParsePiece(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(entry, "id");
        var title = GetString(entry, "title");
        var composer = GetString(entry, "composer");
        var license = GetString(entry, "license");
        var sourceUrl = GetString(entry, "source_url");
        var pdfUrl = GetString(entry, "pdf_url");
        if (id is null || title is null || composer is null || license is null || sourceUrl is null || pdfUrl is null
            || !IsMutopiaUrl(sourceUrl, ".ly") || !IsMutopiaUrl(pdfUrl, ".pdf"))
        {
            return null;
        }

        return new SheetMusicPiece(
            id,
            title,
            composer,
            GetString(entry, "opus"),
            GetString(entry, "instrument"),
            GetString(entry, "style"),
            license,
            sourceUrl,
            pdfUrl);
    }

    private static string? GetString(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxStringLength || text.Any(char.IsControl))
        {
            return null;
        }

        return text.Trim();
    }

    private static bool IsMutopiaUrl(string value, string extension) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("www.mutopiaproject.org", StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443
        && uri.UserInfo.Length == 0
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && uri.AbsolutePath.StartsWith("/ftp/", StringComparison.Ordinal)
        && uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One public-domain or per-piece-licensed Mutopia piece, provenance included.</summary>
public sealed record SheetMusicPiece(
    string Id,
    string Title,
    string Composer,
    string? Opus,
    string? Instrument,
    string? Style,
    string License,
    string SourceUrl,
    string PdfUrl);
