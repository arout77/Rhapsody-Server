namespace RhapsodyServer.Core.Profiling;

/// <summary>One SQL statement seen on the wire between an app and MariaDB.</summary>
public sealed class QueryRecord
{
    public long Id { get; internal set; }
    public long ConnectionId { get; init; }
    public int ClientPort { get; init; }
    /// <summary>Process that owns the client end of the connection (a php-cgi worker, node, phpMyAdmin...). Null if unknown.</summary>
    public int? Pid { get; set; }
    public string? Process { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public string Sql { get; init; } = "";
    public bool Prepared { get; init; }
    /// <summary>Time until MariaDB started answering.</summary>
    public double FirstByteMs { get; set; }
    /// <summary>Time until MariaDB finished sending (grows while a big result set streams).</summary>
    public double TotalMs { get; set; }
    public string? Error { get; set; }
}

/// <summary>One HTTP request as the web server logged it.</summary>
public sealed class RequestRecord
{
    public long Id { get; internal set; }
    public DateTimeOffset StartedAt { get; init; }
    public double DurationMs { get; init; }
    public string Method { get; init; } = "";
    public string Host { get; init; } = "";
    public string Uri { get; init; } = "";
    public int Status { get; init; }
    public long Bytes { get; init; }
    public string RequestId { get; init; } = "";
    public double? UpstreamMs { get; init; }
    public DateTimeOffset EndedAt => StartedAt.AddMilliseconds(DurationMs);
}

public sealed record RequestView(RequestRecord Request, IReadOnlyList<QueryRecord> Queries, double SqlMs);
/// <param name="Page">1-based page of <see cref="Requests"/> (always 1 when the snapshot isn't paged).</param>
/// <param name="PageSize">Requests per page, or 0 for an unpaged snapshot.</param>
/// <param name="StoredRequests">How many requests the store holds right now, across all pages.</param>
public sealed record ProfileSnapshot(bool Enabled, RequestView[] Requests, QueryRecord[] Queries, long TotalRequests, long TotalQueries,
    int Page = 1, int PageSize = 0, int StoredRequests = 0, int PageCount = 1);

/// <summary>
/// Bounded in-memory history. Nothing touches disk and old entries fall off the end, so profiling can't
/// slowly eat memory or storage. Queries are matched to requests by time window until the PHP collector
/// (phase 2) supplies an exact process id + request id.
/// </summary>
public sealed class ProfileStore
{
    readonly object _gate = new();
    readonly int _maxRequests, _maxQueries;
    readonly LinkedList<RequestRecord> _requests = new();
    readonly LinkedList<QueryRecord> _queries = new();
    long _nextRequest, _nextQuery;

    public event Action? Changed;

    public ProfileStore(int maxRequests = 500, int maxQueries = 5000)
    {
        _maxRequests = maxRequests;
        _maxQueries = maxQueries;
    }

    public void Add(QueryRecord q)
    {
        lock (_gate)
        {
            q.Id = ++_nextQuery;
            _queries.AddLast(q);
            while (_queries.Count > _maxQueries) _queries.RemoveFirst();
        }
        Changed?.Invoke();
    }

    public void Add(RequestRecord r)
    {
        lock (_gate)
        {
            r.Id = ++_nextRequest;
            _requests.AddLast(r);
            while (_requests.Count > _maxRequests) _requests.RemoveFirst();
        }
        Changed?.Invoke();
    }

    /// <summary>Raised when something already stored grew (a result set finished streaming, an error arrived).</summary>
    public void Touch() => Changed?.Invoke();

    public void Clear()
    {
        lock (_gate) { _requests.Clear(); _queries.Clear(); }
        Changed?.Invoke();
    }

    static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(5);

    static bool Overlaps(RequestRecord r, QueryRecord q) => q.StartedAt >= r.StartedAt - Slack && q.StartedAt <= r.EndedAt + Slack;

    static RequestView ViewOf(RequestRecord r, List<QueryRecord> all)
    {
        var inside = all.Where(q => Overlaps(r, q)).ToList();
        return new RequestView(r, inside, inside.Sum(q => q.TotalMs));
    }

    public ProfileSnapshot Snapshot(bool enabled, int maxRequests = 100, int maxQueries = 200)
    {
        lock (_gate)
        {
            var reqs = _requests.Reverse().Take(maxRequests).ToList();           // newest first
            var all = _queries.ToList();
            var views = reqs.Select(r => ViewOf(r, all)).ToArray();
            var queries = _queries.Reverse().Take(maxQueries).ToArray();
            return new ProfileSnapshot(enabled, views, queries, _nextRequest, _nextQuery, 1, 0, _requests.Count, 1);
        }
    }

    /// <summary>One page of requests, newest first. A page past the end is clamped to the last one.</summary>
    public ProfileSnapshot SnapshotPage(bool enabled, int page, int pageSize, int maxQueries = 200)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        lock (_gate)
        {
            var stored = _requests.Count;
            var pageCount = Math.Max(1, (stored + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 1, pageCount);
            var reqs = _requests.Reverse().Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var all = _queries.ToList();
            var views = reqs.Select(r => ViewOf(r, all)).ToArray();
            var queries = _queries.Reverse().Take(maxQueries).ToArray();
            return new ProfileSnapshot(enabled, views, queries, _nextRequest, _nextQuery, page, pageSize, stored, pageCount);
        }
    }

    /// <summary>
    /// Deletes one request, and the SQL that was matched only to it (a query that also falls inside another stored
    /// request's window stays, because that request still claims it). False if it is already gone.
    /// </summary>
    public bool Remove(long requestId)
    {
        lock (_gate)
        {
            var node = _requests.First;
            while (node != null && node.Value.Id != requestId) node = node.Next;
            if (node == null) return false;
            var gone = node.Value;
            _requests.Remove(node);

            var q = _queries.First;
            while (q != null)
            {
                var next = q.Next;
                var query = q.Value;
                if (Overlaps(gone, query) && !_requests.Any(r => Overlaps(r, query))) _queries.Remove(q);
                q = next;
            }
        }
        Changed?.Invoke();
        return true;
    }
}
