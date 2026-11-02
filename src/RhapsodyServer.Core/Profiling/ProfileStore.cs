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
public sealed record ProfileSnapshot(bool Enabled, RequestView[] Requests, QueryRecord[] Queries, long TotalRequests, long TotalQueries);

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

    public ProfileSnapshot Snapshot(bool enabled, int maxRequests = 100, int maxQueries = 200)
    {
        lock (_gate)
        {
            var reqs = _requests.Reverse().Take(maxRequests).ToList();           // newest first
            var all = _queries.ToList();
            var views = new List<RequestView>(reqs.Count);
            foreach (var r in reqs)
            {
                var slack = TimeSpan.FromMilliseconds(5);
                var inside = all
                    .Where(q => q.StartedAt >= r.StartedAt - slack && q.StartedAt <= r.EndedAt + slack)
                    .ToList();
                views.Add(new RequestView(r, inside, inside.Sum(q => q.TotalMs)));
            }
            var queries = _queries.Reverse().Take(maxQueries).ToArray();
            return new ProfileSnapshot(enabled, views.ToArray(), queries, _nextRequest, _nextQuery);
        }
    }
}
