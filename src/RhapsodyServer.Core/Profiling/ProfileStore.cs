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

/// <param name="Php">What PHP reported about this request (exact process, memory, files), when the collector saw it.</param>
/// <param name="Browser">What the browser measured loading this page, when the probe reported it.</param>
public sealed record RequestView(RequestRecord Request, IReadOnlyList<QueryRecord> Queries, double SqlMs,
    PhpRecord? Php = null, BrowserRecord? Browser = null);
/// <param name="Page">1-based page of <see cref="Requests"/> (always 1 when the snapshot isn't paged).</param>
/// <param name="PageSize">Requests per page, or 0 for an unpaged snapshot.</param>
/// <param name="StoredRequests">How many requests the store holds right now, across all pages.</param>
public sealed record ProfileSnapshot(bool Enabled, RequestView[] Requests, QueryRecord[] Queries, long TotalRequests, long TotalQueries,
    int Page = 1, int PageSize = 0, int StoredRequests = 0, int PageCount = 1);

/// <summary>
/// Bounded in-memory history. Nothing touches disk and old entries fall off the end, so profiling can't
/// slowly eat memory or storage. A request the PHP collector reported is matched to its queries exactly (the
/// php-cgi worker's process id, inside the request's time window); anything else falls back to the time window alone.
/// </summary>
public sealed class ProfileStore
{
    readonly object _gate = new();
    readonly int _maxRequests, _maxQueries;
    readonly LinkedList<RequestRecord> _requests = new();
    readonly LinkedList<QueryRecord> _queries = new();
    readonly LinkedList<PhpRecord> _php = new();
    readonly Dictionary<string, BrowserRecord> _browser = new();
    readonly Queue<string> _browserOrder = new();
    long _nextRequest, _nextQuery, _nextPhp;

    /// <summary>Random per run. The browser probe sends it back, so a web page on the internet can't plant timings.</summary>
    public string BeaconKey { get; } = PhpCollectorScript.NewBeaconKey();

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

    public void Add(PhpRecord r)
    {
        lock (_gate)
        {
            r.Id = ++_nextPhp;
            _php.AddLast(r);
            while (_php.Count > _maxRequests + 100) _php.RemoveFirst();
        }
        Changed?.Invoke();
    }

    /// <summary>Keeps the newest report per request id (the probe sends a final one when the tab is left).</summary>
    public void Add(BrowserRecord r)
    {
        lock (_gate)
        {
            if (!_browser.ContainsKey(r.RequestId)) _browserOrder.Enqueue(r.RequestId);
            _browser[r.RequestId] = r;
            while (_browserOrder.Count > _maxRequests + 100) _browser.Remove(_browserOrder.Dequeue());
        }
        Changed?.Invoke();
    }

    /// <summary>The recorded request with this id, or null.</summary>
    public RequestRecord? FindRequest(long id)
    {
        lock (_gate) return _requests.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>The newest PHP record for a request id the collector made, or null.</summary>
    public PhpRecord? FindPhp(string requestId)
    {
        if (requestId.Length == 0) return null;
        lock (_gate) return PhpFor(requestId);
    }

    PhpRecord? PhpFor(string requestId)
    {
        for (var n = _php.Last; n != null; n = n.Previous)
            if (n.Value.RequestId == requestId) return n.Value;
        return null;
    }

    /// <summary>Raised when something already stored grew (a result set finished streaming, an error arrived).</summary>
    public void Touch() => Changed?.Invoke();

    public void Clear()
    {
        lock (_gate) { _requests.Clear(); _queries.Clear(); _php.Clear(); _browser.Clear(); _browserOrder.Clear(); }
        Changed?.Invoke();
    }

    static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(5);

    static bool Overlaps(RequestRecord r, QueryRecord q) => q.StartedAt >= r.StartedAt - Slack && q.StartedAt <= r.EndedAt + Slack;

    static bool Within(PhpRecord p, QueryRecord q) => q.StartedAt >= p.StartedAt - Slack && q.StartedAt <= p.EndedAt + Slack;

    RequestView ViewOf(RequestRecord r, List<QueryRecord> all)
    {
        var php = r.RequestId.Length > 0 ? PhpFor(r.RequestId) : null;
        _browser.TryGetValue(r.RequestId, out var browser);
        List<QueryRecord> inside;
        if (php != null && php.Pid > 0)
        {
            // Exact: the queries sent by this request's own php-cgi worker. A query whose owner Windows couldn't name
            // (no pid) is still credited by time, as before.
            inside = all.Where(q => q.Pid == php.Pid ? Within(php, q) : q.Pid == null && Overlaps(r, q)).ToList();
        }
        else inside = all.Where(q => Overlaps(r, q)).ToList();
        return new RequestView(r, inside, inside.Sum(q => q.TotalMs), php, browser);
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
            if (gone.RequestId.Length > 0)
            {
                var pn = _php.Last;
                while (pn != null) { var prev = pn.Previous; if (pn.Value.RequestId == gone.RequestId) _php.Remove(pn); pn = prev; }
                _browser.Remove(gone.RequestId);
            }

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
