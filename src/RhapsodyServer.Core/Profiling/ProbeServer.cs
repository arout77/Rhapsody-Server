using System.Net;
using System.Text;

namespace RhapsodyServer.Core.Profiling;

/// <summary>
/// A tiny loopback-only server for the browser probe: it serves /__rs/probe.js and takes the probe's measurements at
/// /__rs/beacon. Sites reach it through their own address (the web server proxies /__rs/ here), so the browser sees
/// same-origin requests. It is separate from the dashboard so profiling works without the dashboard being open, and it
/// can do nothing but hand out one fixed script and store numbers that carry the right key.
/// </summary>
public sealed class ProbeServer : IDisposable
{
    readonly ProfileStore _store;
    readonly string _tld;
    readonly int _port;
    HttpListener? _listener;
    readonly CancellationTokenSource _cts = new();

    public int Port => _port;

    public ProbeServer(ProfileStore store, int port, string tld) { _store = store; _port = port; _tld = tld; }

    /// <summary>False when the port is taken: profiling then runs without the browser probe.</summary>
    public bool TryStart()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/__rs/");
            _listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            _listener = null;
            return false;
        }
        _ = Task.Run(LoopAsync);
        return true;
    }

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            var req = ctx.Request;
            if (!BrowserProbe.OriginAllowed(req.Headers["Origin"], _tld)) { Send(res, 403, "text/plain", "no"); return; }
            var path = req.Url!.AbsolutePath;
            if (req.HttpMethod == "GET" && path == BrowserProbe.ScriptPath)
            {
                res.Headers["Cache-Control"] = "no-store";
                Send(res, 200, "application/javascript; charset=utf-8", BrowserProbe.Script);
            }
            else if (req.HttpMethod == "GET" && path == TemplateHintsOverlay.ScriptPath)
            {
                res.Headers["Cache-Control"] = "no-store";
                Send(res, 200, "application/javascript; charset=utf-8", TemplateHintsOverlay.Script);
            }
            else if (req.HttpMethod == "POST" && path == BrowserProbe.BeaconPath)
            {
                if (req.ContentLength64 > BrowserProbe.MaxBeaconBytes) { Send(res, 413, "text/plain", "too big"); return; }
                using var rd = new StreamReader(req.InputStream, Encoding.UTF8);
                var buf = new char[BrowserProbe.MaxBeaconBytes + 1];
                var n = await rd.ReadBlockAsync(buf, 0, buf.Length);
                if (BrowserProbe.ParseBeacon(new string(buf, 0, n), _store.BeaconKey) is { } rec) _store.Add(rec);
                Send(res, 204, null, null);          // always the same answer: the page learns nothing about whether it was accepted
            }
            else Send(res, 404, "text/plain", "not found");
        }
        catch { try { res.Abort(); } catch { } }
    }

    static void Send(HttpListenerResponse res, int status, string? type, string? body)
    {
        res.StatusCode = status;
        res.Headers["X-Content-Type-Options"] = "nosniff";
        if (body == null) { res.ContentLength64 = 0; res.Close(); return; }
        var bytes = Encoding.UTF8.GetBytes(body);
        res.ContentType = type;
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Close(); } catch { }
    }
}
