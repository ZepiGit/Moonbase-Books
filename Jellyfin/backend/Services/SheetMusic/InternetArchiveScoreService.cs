using System.Text.Json;

namespace Moonfin.Server.Services;

/// <summary>Searches the Internet Archive's IMSLP collection for score metadata.</summary>
public sealed class InternetArchiveScoreService(HttpClient client)
{
    public async Task<SheetMusicPiece?> ResolveAsync(string identifier, CancellationToken cancellationToken)
    {
        if (identifier.Length is < 1 or > 150
            || !identifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        using var response = await client.GetAsync("https://archive.org/metadata/" + identifier,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_000_000) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 12 },
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
        var title = Text(metadata, "title");
        var creator = Text(metadata, "creator");
        if (title is null || creator is null) return null;
        var collection = metadata.TryGetProperty("collection", out var collections) ? collections : default;
        var isImslp = collection.ValueKind switch
        {
            JsonValueKind.String => collection.GetString() == "imslp",
            JsonValueKind.Array => collection.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String
                && x.GetString() == "imslp"),
            _ => false,
        };
        if (!isImslp) return null;
        var pdf = files.EnumerateArray()
            .Where(f => f.ValueKind == JsonValueKind.Object
                && Text(f, "source") == "original"
                && Text(f, "name") is { } n && n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(Text(f, "size"), out var size) && size is > 100 and <= 100_000_000)
            .Select(f => Text(f, "name"))
            .FirstOrDefault();
        if (pdf is null) return null;
        var rights = Text(metadata, "rights") ?? Text(metadata, "licenseurl") ?? "See source record";
        return new SheetMusicPiece("ia:" + identifier, title, creator, null, null, null,
            rights, "https://archive.org/details/" + identifier,
            "https://archive.org/download/" + identifier + "/" + Uri.EscapeDataString(pdf));
    }

    public async Task<IReadOnlyList<SheetMusicPiece>> SearchAsync(
        string? query, string? composer, CancellationToken cancellationToken)
    {
        var words = new[] { query?.Trim(), composer?.Trim() }
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (words.Length == 0) return [];

        // Archive's advanced search accepts quoted title/creator terms. Remove
        // operators so user input cannot broaden the fixed collection filter.
        static string Safe(string value) => new(value.Where(c => char.IsLetterOrDigit(c)
            || char.IsWhiteSpace(c) || c is '-' or 'ä' or 'ö' or 'ü' or 'ß').Take(120).ToArray());
        var clauses = new List<string> { "collection:(imslp)", "mediatype:(texts)" };
        if (!string.IsNullOrWhiteSpace(query)) clauses.Add($"title:(\"{Safe(query)}\")");
        if (!string.IsNullOrWhiteSpace(composer)) clauses.Add($"creator:(\"{Safe(composer)}\")");
        var url = "https://archive.org/advancedsearch.php?q=" +
            Uri.EscapeDataString(string.Join(" AND ", clauses)) +
            "&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=creator&rows=40&output=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 8 },
            cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("response", out var result)
            || !result.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var pieces = new List<SheetMusicPiece>();
        foreach (var row in docs.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var id = Text(row, "identifier");
            var title = Text(row, "title");
            var author = Text(row, "creator");
            if (id is null || title is null || author is null
                || id.Length > 150 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) continue;
            pieces.Add(new SheetMusicPiece("ia:" + id, title, author, null, null, null,
                "See source record", "https://archive.org/details/" + id, ""));
        }
        return pieces;
    }

    private static string? Text(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array when value.GetArrayLength() > 0 && value[0].ValueKind == JsonValueKind.String
                => value[0].GetString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) || text.Length > 300 || text.Any(char.IsControl)
            ? null : text.Trim();
    }
}
