using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Result of one Shelfmark proxy call: the upstream status plus a body capped to
/// <see cref="MaxResponseBytes"/>, so a runaway upstream can't balloon memory.
/// </summary>
public sealed class ShelfmarkProxyResult
{
    public const int MaxResponseBytes = 1 * 1024 * 1024;

    public HttpStatusCode StatusCode { get; init; }
    public string Body { get; init; } = string.Empty;
    public bool BodyTruncated { get; init; }
    public bool TransportFailed { get; init; }
    public string? TransportError { get; init; }
    public bool IsSuccess => !TransportFailed && !BodyTruncated && (int)StatusCode is >= 200 and < 300;
}

/// <summary>
/// Forwards exactly the five whitelisted Moonfin Books operations to Shelfmark inside the
/// private network. Every call is authenticated with Remote-User/Remote-Groups derived from
/// validated Jellyfin claims; nothing from the inbound request (headers, cookies, tokens)
/// is ever forwarded, and only response JSON bodies are returned.
/// </summary>
public sealed class ShelfmarkProxyService
{
    internal static readonly TimeSpan ShortRequestTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ReleaseSearchTimeout = TimeSpan.FromSeconds(210);
    private const int MaxQueryLength = 200;
    private const int MaxShortParamLength = 400;
    private static readonly HashSet<string> SearchParams = new(StringComparer.Ordinal)
    {
        "query", "content_type", "provider", "limit", "page", "sort",
    };
    private static readonly HashSet<string> ReleaseParams = new(StringComparer.Ordinal)
    {
        "provider", "book_id", "source", "query", "title", "author", "content_type",
        "manual_query", "languages",
    };
    private static readonly HashSet<string> StatusGroups = new(StringComparer.Ordinal)
    {
        "queued", "resolving", "locating", "downloading", "complete", "error", "cancelled",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ShelfmarkProxyService> _logger;

    public ShelfmarkProxyService(IHttpClientFactory httpClientFactory, ILogger<ShelfmarkProxyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    internal static HttpClientHandler CreateHttpHandler() => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
    };

    /// <summary>
    /// Builds the upstream URI for one whitelisted operation. Returns null when a required
    /// parameter is empty, a value is over-long, or a key is outside the operation's allowlist.
    /// Only the four exact GET paths are constructible.
    /// </summary>
    public string? BuildUpstreamPath(
        string operation,
        IReadOnlyDictionary<string, string?> query)
    {
        var path = operation switch
        {
            "status" => "/api/status",
            "search" => "/api/metadata/search",
            "releases" => "/api/releases",
            "active" => "/api/downloads/active",
            _ => null,
        };

        if (path == null)
        {
            return null;
        }

        if ((operation is "status" or "active") && query.Count != 0)
        {
            return null;
        }

        if (operation == "releases" && query.TryGetValue("source", out var requestedSource)
            && requestedSource != "prowlarr")
        {
            return null;
        }

        if (operation == "releases" && query.TryGetValue("provider", out var releaseProvider)
            && releaseProvider is not ("openlibrary" or "manual" or "prowlarr"))
        {
            return null;
        }

        var encoded = new List<string>();
        foreach (var (key, rawValue) in query.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if ((operation == "search" && !SearchParams.Contains(key))
                || (operation == "releases" && !ReleaseParams.Contains(key)))
            {
                return null;
            }

            var value = rawValue?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var maxLength = key.Equals("query", StringComparison.OrdinalIgnoreCase)
                ? MaxQueryLength
                : MaxShortParamLength;
            if (value.Length > maxLength)
            {
                return null;
            }

            var encodedKey = Uri.EscapeDataString(key);
            var encodedValue = Uri.EscapeDataString(value);
            encoded.Add($"{encodedKey}={encodedValue}");
        }

        if (operation == "releases" && !query.ContainsKey("source"))
        {
            encoded.Add("source=prowlarr");
        }

        if (operation == "search"
            && (!query.TryGetValue("query", out var searchTerm) || string.IsNullOrWhiteSpace(searchTerm)))
        {
            return null;
        }

        return encoded.Count == 0 ? path : $"{path}?{string.Join("&", encoded)}";
    }

    public async Task<ShelfmarkProxyResult> GetAsync(
        string shelfmarkBaseUrl,
        string upstreamPath,
        string remoteUser,
        IReadOnlyList<string> remoteGroups,
        CancellationToken cancellationToken)
    {
        var client = CreateClient(upstreamPath == "/api/releases"
            || upstreamPath.StartsWith("/api/releases?", StringComparison.Ordinal));
        using var request = new HttpRequestMessage(HttpMethod.Get, shelfmarkBaseUrl + upstreamPath);
        ApplyIdentity(request, remoteUser, remoteGroups);

        try
        {
            return await SendAsync(client, request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning("Shelfmark request failed");
            return new ShelfmarkProxyResult
            {
                StatusCode = HttpStatusCode.BadGateway,
                TransportFailed = true,
                TransportError = "Shelfmark is unreachable",
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Shelfmark request timed out");
            return new ShelfmarkProxyResult
            {
                StatusCode = HttpStatusCode.GatewayTimeout,
                TransportFailed = true,
                TransportError = "Shelfmark did not answer in time",
            };
        }
    }

    public async Task<ShelfmarkProxyResult> PostJsonAsync(
        string shelfmarkBaseUrl,
        string remoteUser,
        IReadOnlyList<string> remoteGroups,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, shelfmarkBaseUrl + "/api/releases/download");
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        ApplyIdentity(request, remoteUser, remoteGroups);

        try
        {
            return await SendAsync(client, request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning("Shelfmark download request failed");
            return new ShelfmarkProxyResult
            {
                StatusCode = HttpStatusCode.BadGateway,
                TransportFailed = true,
                TransportError = "Shelfmark is unreachable",
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Shelfmark download request timed out");
            return new ShelfmarkProxyResult
            {
                StatusCode = HttpStatusCode.GatewayTimeout,
                TransportFailed = true,
                TransportError = "Shelfmark did not answer in time",
            };
        }
    }

    private HttpClient CreateClient(bool releaseSearch = false) => _httpClientFactory.CreateClient(
        releaseSearch ? "MoonfinBooksShelfmarkReleases" : "MoonfinBooksShelfmark");

    /// <summary>Keep only tasks owned by the caller, including for Shelfmark admins.</summary>
    public static ShelfmarkProxyResult FilterStatusForUser(ShelfmarkProxyResult response, string username)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidStatus();
            }

            var filtered = new Dictionary<string, Dictionary<string, Dictionary<string, JsonElement>>>(StringComparer.Ordinal);
            foreach (var group in document.RootElement.EnumerateObject())
            {
                if (!StatusGroups.Contains(group.Name) || group.Value.ValueKind != JsonValueKind.Object)
                {
                    return InvalidStatus();
                }

                var tasks = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
                foreach (var task in group.Value.EnumerateObject())
                {
                    if (task.Value.ValueKind == JsonValueKind.Object
                        && task.Value.TryGetProperty("username", out var owner)
                        && owner.ValueKind == JsonValueKind.String
                        && string.Equals(owner.GetString(), username, StringComparison.Ordinal))
                    {
                        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                        foreach (var field in new[] { "title", "username", "progress", "format", "content_type" })
                        {
                            if (task.Value.TryGetProperty(field, out var value))
                            {
                                fields[field] = value.Clone();
                            }
                        }

                        tasks[DisplayTaskId(username, task.Name)] = fields;
                    }
                }

                filtered[group.Name] = tasks;
            }

            return new ShelfmarkProxyResult { StatusCode = response.StatusCode, Body = JsonSerializer.Serialize(filtered) };
        }
        catch (JsonException)
        {
            return InvalidStatus();
        }
    }

    /// <summary>Intersect global active IDs with the caller's validated queue tasks.</summary>
    public static ShelfmarkProxyResult FilterActiveForUser(
        ShelfmarkProxyResult active, ShelfmarkProxyResult status, string username)
    {
        var ownedStatus = FilterStatusForUser(status, username);
        if (!ownedStatus.IsSuccess)
        {
            return ownedStatus;
        }

        try
        {
            using var activeJson = JsonDocument.Parse(active.Body);
            using var statusJson = JsonDocument.Parse(ownedStatus.Body);
            if (activeJson.RootElement.ValueKind != JsonValueKind.Object
                || !activeJson.RootElement.TryGetProperty("active_downloads", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                return InvalidStatus();
            }

            var ownedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in statusJson.RootElement.EnumerateObject())
            {
                foreach (var task in group.Value.EnumerateObject())
                {
                    ownedIds.Add(task.Name);
                }
            }

            var filtered = new List<string>();
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String)
                {
                    return InvalidStatus();
                }

                if (id.GetString() is { } value && ownedIds.Contains(DisplayTaskId(username, value)))
                {
                    filtered.Add(DisplayTaskId(username, value));
                }
            }

            return new ShelfmarkProxyResult
            {
                StatusCode = active.StatusCode,
                Body = JsonSerializer.Serialize(new { active_downloads = filtered }),
            };
        }
        catch (JsonException)
        {
            return InvalidStatus();
        }
    }

    internal static string DisplayTaskId(string username, string rawId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username + "\0" + rawId))).ToLowerInvariant();

    private static ShelfmarkProxyResult InvalidStatus() => new()
    {
        StatusCode = HttpStatusCode.BadGateway,
        TransportFailed = true,
        TransportError = "Invalid Shelfmark status response",
    };

    private static void ApplyIdentity(HttpRequestMessage request, string remoteUser, IReadOnlyList<string> remoteGroups)
    {
        request.Headers.Remove("Remote-User");
        request.Headers.TryAddWithoutValidation("Remote-User", remoteUser);

        request.Headers.Remove("Remote-Groups");
        if (remoteGroups.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Remote-Groups", string.Join(",", remoteGroups));
        }
    }

    private static async Task<ShelfmarkProxyResult> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        var buffer = new byte[ShelfmarkProxyResult.MaxResponseBytes + 1];
        var total = 0;
        var truncated = false;

        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(total, buffer.Length - total),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

        }

        truncated = total > ShelfmarkProxyResult.MaxResponseBytes;

        return new ShelfmarkProxyResult
        {
            StatusCode = response.StatusCode,
            Body = truncated ? string.Empty : Encoding.UTF8.GetString(buffer, 0, total),
            BodyTruncated = truncated,
        };
    }
}
