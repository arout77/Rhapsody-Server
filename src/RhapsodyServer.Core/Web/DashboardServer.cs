using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RhapsodyServer.Core.Ai;

namespace RhapsodyServer.Core.Web;

/// <summary>
/// The dashboard's HTTP server. Started lazily (only when you open the dashboard), bound to 127.0.0.1 only.
///
/// A local web server that can start processes is a target for any web page you happen to visit, so:
///  - every /api request needs the per-install token (header; the SSE stream also accepts ?t=)
///  - the Host header must be 127.0.0.1:PORT or localhost:PORT  (defeats DNS rebinding)
///  - a browser-supplied Origin must be this same origin          (defeats cross-site requests)
///  - the page is served with a strict CSP, nonce-based, no external anything
/// </summary>
public sealed class DashboardServer : IDisposable
{
    readonly IDashboardBackend _backend;
    readonly string _token;
    HttpListener _listener = new();
    readonly CancellationTokenSource _cts = new();
    readonly List<SseClient> _clients = new();
    readonly object _opLock = new();
    readonly SemaphoreSlim _pickGate = new(1, 1);   // one native folder window at a time
    OpInfo? _op;
    Timer? _ping;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/";
    /// <summary>What the tray opens. The token rides in the fragment, which browsers never send to servers.</summary>
    public string OpenUrl => $"{Url}#t={_token}";

    sealed record OpInfo(string Title, string Message, bool Running, string? Error);
    sealed class SseClient { public required HttpListenerResponse Res { get; init; } public required Stream Out { get; init; } }
    sealed class Sink(Action<string> on) : IProgress<string> { public void Report(string v) => on(v); }

    public DashboardServer(IDashboardBackend backend, string token)
    {
        _backend = backend;
        _token = token;
        _backend.Changed += () => Broadcast("changed", "{}");
        _backend.ProfileChanged += PushProfileSoon;
    }

    int _profilePending;
    /// <summary>A busy site can log hundreds of queries a second; the browser gets at most ~5 nudges a second.</summary>
    void PushProfileSoon()
    {
        if (Interlocked.Exchange(ref _profilePending, 1) == 1) return;
        _ = Task.Delay(200).ContinueWith(_ => { Interlocked.Exchange(ref _profilePending, 0); Broadcast("profile", "{}"); });
    }

    public void Start(int preferredPort)
    {
        Port = preferredPort;
        try { Listen(); }
        catch (HttpListenerException) { Port = FreePort(); Listen(); }   // preferred port taken: pick another
        _ = Task.Run(AcceptLoopAsync);
    }

    void Listen()
    {
        _listener.Close();
        _listener = new HttpListener();
        _listener.Prefixes.Add(Url);
        _listener.Prefixes.Add($"http://localhost:{Port}/");   // both names work without admin rights
        _listener.Start();
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }   // listener closed
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    // ------------------------------------------------------------------ guards
    bool HostOk(HttpListenerRequest r)
    {
        var host = r.Headers["Host"] ?? "";
        return host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase);
    }

    bool OriginOk(string origin) =>
        origin.Equals($"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
        || origin.Equals($"http://localhost:{Port}", StringComparison.OrdinalIgnoreCase);

    bool TokenOk(HttpListenerRequest r, bool allowQuery)
    {
        var supplied = r.Headers["X-Rhapsody-Server-Token"] ?? (allowQuery ? r.QueryString["t"] : null) ?? "";
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_token));
    }

    // ----------------------------------------------------------------- routing
    async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!HostOk(req)) { await SendAsync(res, 403, Err("Unexpected Host header")); return; }
            if (req.Headers["Origin"] is { } origin && !OriginOk(origin)) { await SendAsync(res, 403, Err("Cross-origin request refused")); return; }

            var path = req.Url!.AbsolutePath;
            var method = req.HttpMethod;

            if (method == "GET" && (path == "/" || path == "/index.html")) { await SendPageAsync(res); return; }
            if (method == "GET" && path.StartsWith("/assets/")) { await SendAssetAsync(res, path["/assets/".Length..]); return; }
            if (!path.StartsWith("/api/")) { await SendAsync(res, 404, Err("Not found")); return; }
            if (!TokenOk(req, allowQuery: path == "/api/events")) { await SendAsync(res, 401, Err("Missing or wrong token. Open Rhapsody Server from the tray icon.")); return; }

            var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);   // ["api", ...]
            var a1 = seg.Length > 1 ? seg[1] : "";
            var a2 = seg.Length > 2 ? seg[2] : "";
            var a3 = seg.Length > 3 ? seg[3] : "";

            switch (method, a1)
            {
                case ("GET", "state"):
                    await SendAsync(res, 200, new { state = _backend.GetState(), op = _op });
                    return;

                case ("GET", "events"):
                    await OpenEventStreamAsync(res);
                    return;

                case ("GET", "versions"):
                    await SendAsync(res, 200, await _backend.GetVersionsAsync(_cts.Token));
                    return;

                case ("GET", "logs") when Regex.IsMatch(a2, @"^[a-z0-9.\-]+$"):
                    var lines = int.TryParse(req.QueryString["lines"], out var n) ? n : 200;
                    await SendAsync(res, 200, new { text = _backend.ReadLog(a2, lines) });
                    return;

                case ("POST", "services") when a2 == "start-all":
                    await Reply(res, await RunOp("Starting everything", (p, ct) => _backend.StartAllAsync(p, ct)));
                    return;
                case ("POST", "services") when a2 == "stop-all":
                    await Reply(res, await RunOp("Stopping everything", (p, ct) => _backend.StopAllAsync(p, ct)));
                    return;
                case ("POST", "services") when a3 == "start" && Regex.IsMatch(a2, @"^[a-z0-9.\-]+$"):
                    await Reply(res, await RunOp($"Starting {a2}", (p, ct) => _backend.StartServiceAsync(a2, p, ct)));
                    return;
                case ("POST", "services") when a3 == "stop" && Regex.IsMatch(a2, @"^[a-z0-9.\-]+$"):
                    await Reply(res, await RunOp($"Stopping {a2}", (p, ct) => _backend.StopServiceAsync(a2, p, ct)));
                    return;

                case ("POST", "sites") when a2 == "scaffold":
                {
                    var b = await Body<ScaffoldRequestDto>(req);
                    await Reply(res, await RunOp("Creating Rhapsody site", (p, ct) => _backend.ScaffoldAsync(b, p, ct)));
                    return;
                }
                case ("POST", "sites") when a2 == "":
                {
                    var b = await Body<SiteRequest>(req);
                    await Reply(res, await RunOp("Adding site", (p, ct) => _backend.AddSiteAsync(b, p, ct)));
                    return;
                }
                case ("PUT", "sites") when a3 == "php":
                {
                    var b = await Body<PhpRequest>(req);
                    await Reply(res, await RunOp($"Switching {a2} to PHP {b.Php}", (p, ct) => _backend.SetSitePhpAsync(a2, b.Php, p, ct)));
                    return;
                }
                case ("DELETE", "sites") when a2 != "":
                    await Reply(res, await RunOp("Removing site", (p, ct) => _backend.RemoveSiteAsync(a2, p, ct)));
                    return;

                case ("POST", "webserver"):
                {
                    var b = await Body<WebServerRequest>(req);
                    await Reply(res, await RunOp("Switching web server", (p, ct) => _backend.SwitchWebServerAsync(b.Kind, p, ct)));
                    return;
                }

                case ("POST", "versions") when a2 == "install":
                {
                    var b = await Body<VersionRequest>(req);
                    await Reply(res, await RunOp($"Installing {b.Id} {b.Series}", (p, ct) => _backend.InstallVersionAsync(b.Id, b.Series, p, ct)));
                    return;
                }
                case ("POST", "versions") when a2 == "activate":
                {
                    var b = await Body<VersionRequest>(req);
                    await Reply(res, await RunOp($"Switching {b.Id} to {b.Series}", (p, ct) => _backend.SetActiveVersionAsync(b.Id, b.Series, p, ct)));
                    return;
                }
                case ("POST", "versions") when a2 == "update":
                {
                    var b = await Body<VersionRequest>(req);
                    await Reply(res, await RunOp($"Updating {b.Id} {b.Series}", (p, ct) => _backend.UpdateVersionAsync(b.Id, b.Series, p, ct)));
                    return;
                }
                case ("POST", "versions") when a2 == "import-apache":
                {
                    // The browser sends the zip as the raw request body; its name rides in the query string.
                    var name = req.QueryString["name"] ?? "";
                    if (req.ContentLength64 > Stack.MaxImportBytes) { await SendAsync(res, 413, Err("That file is far bigger than an Apache download.")); return; }
                    await Reply(res, await RunOp("Installing Apache from a file", (p, ct) => _backend.ImportApacheAsync(name, req.InputStream, p, ct)));
                    return;
                }
                case ("POST", "versions") when a2 == "remove":
                {
                    var b = await Body<VersionRequest>(req);
                    await Reply(res, await RunOp($"Removing {b.Id} {b.Series}", (p, ct) => _backend.RemoveVersionAsync(b.Id, b.Series, p, ct)));
                    return;
                }

                case ("GET", "profile"):
                    await SendAsync(res, 200, _backend.GetProfile());
                    return;
                case ("POST", "profile") when a2 == "clear":
                    _backend.ClearProfile();
                    await SendAsync(res, 200, new { ok = true });
                    return;
                case ("POST", "profile") when a2 == "enable":
                    await Reply(res, await RunOp("Turning profiling on", (p, ct) => _backend.SetProfilingAsync(true, p, ct)));
                    return;
                case ("POST", "profile") when a2 == "disable":
                    await Reply(res, await RunOp("Turning profiling off", (p, ct) => _backend.SetProfilingAsync(false, p, ct)));
                    return;

                case ("GET", "modules") when a2 == "php":
                    await SendAsync(res, 200, _backend.GetPhpModules(req.QueryString["branch"]));
                    return;
                case ("GET", "modules") when a2 == "apache":
                    await SendAsync(res, 200, _backend.GetApacheModules());
                    return;
                case ("PUT", "modules") when a2 == "php" && Regex.IsMatch(a3, @"^\d+\.\d+$"):
                {
                    var b = await Body<ModulesRequest>(req);
                    await Reply(res, await RunOp($"Updating PHP {a3} extensions", (p, ct) => _backend.SetPhpExtensionsAsync(a3, b.Enabled ?? Array.Empty<string>(), p, ct)));
                    return;
                }
                case ("PUT", "modules") when a2 == "apache":
                {
                    var b = await Body<ModulesRequest>(req);
                    await Reply(res, await RunOp("Updating Apache modules", (p, ct) => _backend.SetApacheModulesAsync(b.Enabled ?? Array.Empty<string>(), p, ct)));
                    return;
                }

                case ("POST", "pick-folder"):
                {
                    if (!_backend.CanPickFolder) { await SendAsync(res, 501, Err("This host can't show a folder window. Type the path instead.")); return; }
                    if (!await _pickGate.WaitAsync(0)) { await SendAsync(res, 409, Err("A folder window is already open.")); return; }
                    try
                    {
                        var b = req.HasEntityBody ? await Body<PickFolderRequest>(req) : new PickFolderRequest(null);
                        var chosen = await _backend.PickFolderAsync(b.Initial, _cts.Token);   // waits while the window is open
                        await SendAsync(res, 200, new { path = chosen });
                    }
                    finally { _pickGate.Release(); }
                    return;
                }

                case ("GET", "ai"):
                    await SendAsync(res, 200, _backend.GetAi());
                    return;
                case ("PUT", "ai") when a2 == "":
                {
                    if (req.ContentLength64 > MaxAiBody) { await SendAsync(res, 413, Err("That request is too large.")); return; }
                    _backend.SaveAi(await Body<AiSaveInput>(req));
                    await SendAsync(res, 200, _backend.GetAi());
                    return;
                }
                case ("POST", "ai") when a2 == "test":
                {
                    var b = req.HasEntityBody ? await Body<AiTestRequest>(req) : new AiTestRequest(null);
                    await SendAsync(res, 200, await _backend.TestAiAsync(b.Which ?? AiService.Primary, _cts.Token));
                    return;
                }
                case ("POST", "ai") when a2 == "explain":
                {
                    if (req.ContentLength64 > MaxAiBody) { await SendAsync(res, 413, Err("That request is too large.")); return; }
                    await SendAsync(res, 200, await _backend.ExplainAsync(await Body<ExplainRequest>(req), _cts.Token));
                    return;
                }

                case ("POST", "hosts") when a2 == "grant":
                    await Reply(res, await RunOp("Granting hosts-file access", (p, ct) => _backend.GrantHostsAccessAsync(p, ct)));
                    return;
            }
            await SendAsync(res, 404, Err("Unknown endpoint"));
        }
        catch (JsonException) { await TrySend(res, 400, Err("Malformed request body")); }
        catch (AiException ex) { await TrySend(res, ex.Busy ? 409 : 400, Err(ex.Message)); }   // a provider problem, worded for the person
        catch (ArgumentException ex) { await TrySend(res, 400, Err(ex.Message)); }              // input the person can fix
        catch (Exception ex) { await TrySend(res, 500, Err(ex.Message)); }
    }

    const long MaxAiBody = 256 * 1024;

    static async Task<T> Body<T>(HttpListenerRequest r) =>
        await JsonSerializer.DeserializeAsync<T>(r.InputStream, Json) ?? throw new JsonException("empty body");

    static object Err(string message) => new { error = message };

    async Task Reply(HttpListenerResponse res, (int Status, object Body) r) => await SendAsync(res, r.Status, r.Body);
    async Task TrySend(HttpListenerResponse res, int status, object body) { try { await SendAsync(res, status, body); } catch { } }

    // -------------------------------------------------------------- operations
    /// <summary>Runs one operation at a time. The HTTP request waits for it; progress flows over the event stream.</summary>
    async Task<(int, object)> RunOp(string title, Func<IProgress<string>, CancellationToken, Task> work)
    {
        lock (_opLock)
        {
            if (_op is { Running: true }) return (409, Err($"Busy: {_op.Title}. Wait for it to finish."));
            _op = new OpInfo(title, "", true, null);
        }
        Broadcast("op", JsonSerializer.Serialize(_op, Json));

        string? error = null;
        try { await work(new Sink(m => { lock (_opLock) _op = new OpInfo(title, m, true, null); Broadcast("op", JsonSerializer.Serialize(_op, Json)); }), _cts.Token); }
        catch (Exception ex) { error = ex.Message; }

        lock (_opLock) _op = new OpInfo(title, "", false, error);
        Broadcast("op", JsonSerializer.Serialize(_op, Json));
        Broadcast("changed", "{}");
        return error == null ? (200, new { ok = true }) : (400, Err(error));
    }

    // -------------------------------------------------------------- responses
    static async Task SendAsync(HttpListenerResponse res, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    static readonly string PageTemplate = LoadPage();
    static string LoadPage()
    {
        using var s = typeof(DashboardServer).Assembly.GetManifestResourceStream("index.html")
            ?? throw new InvalidOperationException("index.html is not embedded");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    static async Task SendPageAsync(HttpListenerResponse res)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var bytes = Encoding.UTF8.GetBytes(PageTemplate.Replace("__NONCE__", nonce));
        res.StatusCode = 200;
        res.ContentType = "text/html; charset=utf-8";
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers["Referrer-Policy"] = "no-referrer";
        res.Headers["Content-Security-Policy"] =
            $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; " +
            "img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    // ------------------------------------------------------------------ assets
    static readonly HashSet<string> Assets = new() { "logo.png", "icon.png" };

    /// <summary>The logo and icon, embedded in the app. A fixed allow-list, so a path can never reach anything else.</summary>
    static async Task SendAssetAsync(HttpListenerResponse res, string name)
    {
        if (!Assets.Contains(name)) { await SendAsync(res, 404, Err("Not found")); return; }
        await using var s = typeof(DashboardServer).Assembly.GetManifestResourceStream("assets/" + name);
        if (s == null) { await SendAsync(res, 404, Err("Not found")); return; }
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        res.StatusCode = 200;
        res.ContentType = "image/png";
        res.Headers["Cache-Control"] = "public, max-age=86400";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.ContentLength64 = ms.Length;
        await res.OutputStream.WriteAsync(ms.ToArray());
        res.Close();
    }

    // ------------------------------------------------------------ event stream
    async Task OpenEventStreamAsync(HttpListenerResponse res)
    {
        res.StatusCode = 200;
        res.ContentType = "text/event-stream";
        res.Headers["Cache-Control"] = "no-cache";
        res.SendChunked = true;
        var client = new SseClient { Res = res, Out = res.OutputStream };
        var hello = Encoding.UTF8.GetBytes(": connected\n\n");
        await client.Out.WriteAsync(hello);
        await client.Out.FlushAsync();
        lock (_clients)
        {
            _clients.Add(client);
            _ping ??= new Timer(_ => Broadcast(null, "ping"), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        }
        // The response stays open; Broadcast writes to it and drops it when the browser goes away.
    }

    void Broadcast(string? evt, string data)
    {
        var text = evt == null ? ": ping\n\n" : $"event: {evt}\ndata: {data.Replace("\n", " ")}\n\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        List<SseClient> snapshot;
        lock (_clients) snapshot = _clients.ToList();
        foreach (var c in snapshot)
        {
            try { c.Out.Write(bytes, 0, bytes.Length); c.Out.Flush(); }
            catch
            {
                lock (_clients) _clients.Remove(c);
                try { c.Res.Abort(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _ping?.Dispose();
        lock (_clients) { foreach (var c in _clients) { try { c.Res.Abort(); } catch { } } _clients.Clear(); }
        try { _listener.Close(); } catch { }
    }
}
