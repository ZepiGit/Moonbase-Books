using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Moonfin.Server.Services;

/// <summary>Short-lived, user-bound handles for Shelfmark source IDs, which may contain keys.</summary>
public sealed class BooksReleaseHandles(IDataProtectionProvider provider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public string Issue(string username, string sourceId) => Issue(username, sourceId, Lifetime);

    internal string Issue(string username, string sourceId, TimeSpan lifetime) =>
        provider.CreateProtector("Moonfin.Books.ReleaseSource", username)
            .ToTimeLimitedDataProtector().Protect(sourceId, lifetime);

    public bool TryRead(string username, string handle, out string sourceId)
    {
        sourceId = string.Empty;
        if (handle.Length is < 1 or > 8192)
        {
            return false;
        }

        try
        {
            sourceId = provider.CreateProtector("Moonfin.Books.ReleaseSource", username)
                .ToTimeLimitedDataProtector().Unprotect(handle);
            return sourceId.Length is > 0 and <= 2048 && !sourceId.Any(char.IsControl);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>Bounded, per-user in-memory release lookups.</summary>
public sealed class BooksReleaseJobs(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(210);

    public bool TryStart(string username, Func<CancellationToken, Task<ShelfmarkProxyResult>> work, out string jobId)
    {
        lock (_gate)
        {
            Cleanup();
            if (_jobs.Count >= 32 || _jobs.Values.Count(x => !x.Result.IsCompleted) >= 4)
            {
                jobId = string.Empty;
                return false;
            }

            jobId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _jobs.Add(jobId, new Job(username, _clock.GetUtcNow() + Lifetime, Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(UpstreamTimeout);
                try
                {
                    return await work(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Failure(HttpStatusCode.GatewayTimeout, "Shelfmark did not answer in time");
                }
                catch (Exception)
                {
                    return Failure(HttpStatusCode.BadGateway, "Shelfmark request failed");
                }
            })));
            return true;
        }
    }

    public Task<ShelfmarkProxyResult>? Find(string username, string jobId)
    {
        lock (_gate)
        {
            Cleanup();
            return _jobs.TryGetValue(jobId, out var job)
                && string.Equals(job.Username, username, StringComparison.Ordinal) ? job.Result : null;
        }
    }

    private void Cleanup()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _jobs.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray())
        {
            _jobs.Remove(key);
        }
    }

    private static ShelfmarkProxyResult Failure(HttpStatusCode status, string error) => new()
    {
        StatusCode = status, TransportFailed = true, TransportError = error,
    };

    private sealed record Job(string Username, DateTimeOffset Expires, Task<ShelfmarkProxyResult> Result);
}
