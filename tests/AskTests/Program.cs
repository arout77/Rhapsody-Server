using System.Net;
using System.Text;
using System.Text.Json;
using RhapsodyServer.Core.Ai;
using RhapsodyServer.Core.Profiling;

int failed = 0, passed = 0;
void Check(bool ok, string name, string? detail = null)
{
    if (ok) { passed++; Console.WriteLine("  ok    " + name); }
    else { failed++; Console.WriteLine("  FAIL  " + name + (detail != null ? "\n        " + detail : "")); }
}

// ------------------------------------------------------------------ a fake project on disk
var tmp = Path.Combine(Path.GetTempPath(), "ask-tests-" + Guid.NewGuid().ToString("N")[..8]);
var siteRoot = Path.Combine(tmp, "shop");
Directory.CreateDirectory(Path.Combine(siteRoot, "app", "Controllers"));
Directory.CreateDirectory(Path.Combine(siteRoot, ".git"));
Directory.CreateDirectory(Path.Combine(siteRoot, "vendor", "x"));
File.WriteAllText(Path.Combine(siteRoot, "app", "Controllers", "ProductController.php"),
    "<?php\nclass ProductController {\n  public function show($id) {\n    $password = $_POST['password'];\n" +
    "    $cfg = ['host' => 'db', 'password' => 'hunter2secret'];\n    foreach ($reviews as $r) { $r->author(); }\n  }\n}\n");
File.WriteAllText(Path.Combine(siteRoot, ".env"), "APP_ENV=local\nDB_HOST=127.0.0.1\nDB_PASS=supersecretvalue\nAPP_KEY=base64:abcdefghijklmnopqrstuvwxyz0123456789=\n");
File.WriteAllText(Path.Combine(siteRoot, ".git", "config"), "[remote]\nurl = https://user:tok3n@github.com/x/y.git\n");
File.WriteAllText(Path.Combine(siteRoot, "vendor", "x", "Lib.php"), "<?php // needle_in_vendor\n");
File.WriteAllBytes(Path.Combine(siteRoot, "logo.png"), new byte[] { 0x89, 0x50, 0, 0, 1, 2 });
File.WriteAllText(Path.Combine(tmp, "outside.txt"), "TOP SECRET OUTSIDE");
var linkMade = false;
try { Directory.CreateSymbolicLink(Path.Combine(siteRoot, "escape"), tmp); linkMade = true; } catch { }

// ------------------------------------------------------------------ a fake stack
var store = new ProfileStore();
var t0 = DateTimeOffset.UtcNow.AddSeconds(-5);
store.Add(new RequestRecord { StartedAt = t0.AddSeconds(-2), DurationMs = 120, Method = "GET", Host = "shop.test", Uri = "/", Status = 200, Bytes = 900, UpstreamMs = 100 });
store.Add(new RequestRecord { StartedAt = t0, DurationMs = 1840, Method = "GET", Host = "shop.test", Uri = "/products/123", Status = 200, Bytes = 5300, UpstreamMs = 1790 });
store.Add(new RequestRecord { StartedAt = t0.AddSeconds(3), DurationMs = 80, Method = "GET", Host = "shop.test", Uri = "/checkout", Status = 500, Bytes = 400, UpstreamMs = 70 });
store.Add(new QueryRecord { StartedAt = t0.AddMilliseconds(10), Sql = "SELECT * FROM products WHERE id = 123", TotalMs = 300, FirstByteMs = 290 });
for (var i = 0; i < 43; i++)
    store.Add(new QueryRecord { StartedAt = t0.AddMilliseconds(320 + i * 30), Sql = $"SELECT * FROM reviews WHERE product_id = {100 + i} AND note = 'a''b{i}'", TotalMs = 28, FirstByteMs = 27 });
store.Add(new QueryRecord { StartedAt = t0.AddSeconds(3).AddMilliseconds(5), Sql = "INSERT INTO orders (a) VALUES (1)", TotalMs = 3, Error = "Table 'shop.orders' doesn't exist" });

var ctx = new FakeCtx(store, siteRoot);
var redactor = new AiRedactor(null, "someuser");
var tools = DevTools.Build(ctx, redactor, allowRequests: true);
AgentTool T(string n) => tools.First(t => t.Name == n);
async Task<string> Run(string tool, object args) => await T(tool).Run(JsonSerializer.SerializeToElement(args), CancellationToken.None);

Console.WriteLine("Profiler tools");
var rr = await Run("recent_requests", new { uri_contains = "/products/123" });
Check(rr.Contains("#2") && rr.Contains("1.84s") && rr.Contains("44 queries"), "recent_requests finds the request with timings and query count", rr);
var rs = await Run("recent_requests", new { min_status = 500 });
Check(rs.Contains("/checkout") && !rs.Contains("/products"), "recent_requests filters by status", rs);
var slowest = await Run("recent_requests", new { sort = "slowest", limit = 1 });
Check(slowest.Contains("/products/123") && !slowest.Contains("/checkout"), "recent_requests sorts slowest first and honours limit", slowest);
var det = await Run("request_detail", new { id = 2 });
Check(det.Contains("x43") && det.Contains("N+1"), "request_detail groups the 43 repeated lookups and names the N+1 pattern", det);
Check(det.Contains("product_id = ?"), "request_detail shows the query shape with values replaced", det);
var det3 = await Run("request_detail", new { id = 3 });
Check(det3.Contains("doesn't exist"), "request_detail shows SQL errors", det3);
var sq = await Run("slow_queries", new { });
Check(sq.Contains("x43") && sq.Contains("reviews"), "slow_queries ranks the repeated statement by total time", sq);
try { await Run("request_detail", new { id = 999 }); Check(false, "unknown request id is refused"); }
catch (ArgumentException) { Check(true, "unknown request id is refused"); }
ctx.Enabled = false;
Check((await Run("recent_requests", new { })).Contains("profiler is off"), "tools say so plainly when the profiler is off");
ctx.Enabled = true;

Console.WriteLine("File tools");
var rf = await Run("read_file", new { site = "shop", path = "app/Controllers/ProductController.php" });
Check(rf.Contains("$password = $_POST['password']"), "source code stays readable (variable named password is not mangled)", rf);
Check(!rf.Contains("hunter2secret") && rf.Contains("[REDACTED]"), "a quoted password literal in a PHP array is hidden", rf);
Check(rf.Contains("    4| "), "lines are numbered", rf);
var env = await Run("read_file", new { site = "shop", path = ".env" });
Check(!env.Contains("supersecretvalue") && !env.Contains("abcdefghijklmnopqrstuvwxyz") && env.Contains("APP_ENV=local"), ".env: secrets hidden, the rest kept", env);
foreach (var bad in new[] { "../outside.txt", "..\\outside.txt", "/etc/passwd", "app/../../outside.txt", "C:\\Windows\\win.ini", ".git/config", "app/Controllers/x.php:stream" })
{
    try { var o = await Run("read_file", new { site = "shop", path = bad }); Check(false, $"refuses '{bad}'", o); }
    catch (ArgumentException) { Check(true, $"refuses '{bad}'"); }
}
if (linkMade)
{
    try { var o = await Run("read_file", new { site = "shop", path = "escape/outside.txt" }); Check(false, "refuses a path through a symlink", o); }
    catch (ArgumentException) { Check(true, "refuses a path through a symlink"); }
}
try { await Run("read_file", new { site = "shop", path = "logo.png" }); Check(false, "refuses a binary file"); }
catch (ArgumentException e) { Check(e.Message.Contains("binary"), "refuses a binary file"); }
try { await Run("read_file", new { site = "nosuch", path = "x" }); Check(false, "unknown site refused"); }
catch (ArgumentException e) { Check(e.Message.Contains("shop"), "unknown site refused, and the real names are offered"); }
var ld = await Run("list_dir", new { site = "shop" });
Check(ld.Contains("app/") && ld.Contains(".env") && ld.Contains("vendor/"), "list_dir shows the project root", ld);
var sf = await Run("search_files", new { site = "shop", text = "author()" });
Check(sf.Contains("ProductController.php:6"), "search_files finds a call with file:line", sf);
var sv = await Run("search_files", new { site = "shop", text = "needle_in_vendor" });
Check(sv.Contains("not found"), "search skips vendor by default", sv);
var sv2 = await Run("search_files", new { site = "shop", text = "needle_in_vendor", path = "vendor" });
Check(sv2.Contains("Lib.php:1"), "search looks in vendor when pointed there", sv2);
var sg = await Run("search_files", new { site = "shop", text = "tok3n" });
Check(sg.Contains("not found"), "search never reads .git", sg);

Console.WriteLine("Logs and pages");
var lg = await Run("read_log", new { log = "nginx", filter = "fatal" });
Check(lg.Contains("1 matching") && lg.Contains("Call to undefined"), "read_log filter keeps matching lines", lg);
try { await Run("read_log", new { log = "../../etc/passwd" }); Check(false, "read_log only takes known log names"); }
catch (ArgumentException) { Check(true, "read_log only takes known log names"); }
var pg = await Run("load_page", new { site = "shop", path = "/products/123" });
Check(pg.Contains("-> 200") && pg.Contains("Page text"), "load_page reports status and text", pg);
try { await Run("load_page", new { site = "shop", path = "http://evil.example/x" }); Check(false, "load_page refuses a full URL"); }
catch (ArgumentException) { Check(true, "load_page refuses a full URL"); }
try { await Run("load_page", new { site = "shop", path = "//evil.example/x" }); Check(false, "load_page refuses //host"); }
catch (ArgumentException) { Check(true, "load_page refuses //host"); }
Check(!DevTools.Build(ctx, redactor, false).Any(t => t.Name == "load_page"), "load_page is absent unless the person allowed it");

// ------------------------------------------------------------------ the loop with a scripted model
Console.WriteLine("Agent loop");
ToolCall Call(string name, object args, string? id = null) => new(id ?? "c" + Guid.NewGuid().ToString("N")[..8], name, JsonSerializer.Serialize(args));
ChatTurn Say(string text) => new(text, Array.Empty<ToolCall>(), "Fake", "fake-1", 5, false);
ChatTurn Use(params ToolCall[] calls) => new("", calls, "Fake", "fake-1", 5, false);

AgentModel Script(List<IReadOnlyList<ChatMessage>> seen, List<int> toolCounts, params Func<IReadOnlyList<ChatMessage>, ChatTurn>[] steps)
{
    var i = 0;
    return (msgs, specs, ct) =>
    {
        seen.Add(msgs.ToList()); toolCounts.Add(specs.Count);
        var turn = steps[Math.Min(i++, steps.Length - 1)](msgs);
        return Task.FromResult((turn, false, (string?)null));
    };
}

var question = "Why does /products/123 load slow?";
var req = new AgentRequest(question, tools, redactor.Apply, AiAgent.SystemPrompt);

{
    var seen = new List<IReadOnlyList<ChatMessage>>(); var counts = new List<int>(); var live = new List<string>();
    var model = Script(seen, counts,
        _ => Use(Call("recent_requests", new { uri_contains = "/products/123" })),
        _ => Use(Call("request_detail", new { id = 2 }), Call("request_detail", new { id = 2 })),   // second is a duplicate
        _ => Use(Call("read_file", new { site = "shop", path = "app/Controllers/ProductController.php" }), Call("nope", new { })),
        _ => Say("Cause: N+1 in ProductController::show. Evidence: request #2, 43 repeated queries."));
    var res = await AiAgent.RunAsync(req, model, new Progress<string>(live.Add), CancellationToken.None);
    await Task.Delay(50);
    Check(res.Answer.StartsWith("Cause: N+1") && res.ModelCalls == 4 && !res.HitLimit, "runs lookups and ends with the model's answer", $"{res.ModelCalls} calls: {res.Answer}");
    Check(res.Steps.Count == 5, "records every lookup as a step", res.Steps.Count.ToString());
    Check(res.Steps[2].Failed && res.Steps[2].Result.Contains("already ran"), "an identical repeated lookup is refused, not re-run");
    Check(res.Steps[4].Failed && res.Steps[4].Result.Contains("no tool called"), "an unknown tool name is reported back to the model");
    var lastMsgs = seen[^1];
    var toolMsgs = lastMsgs.Where(m => m.Role == "tool").ToList();
    Check(toolMsgs.Count == 5 && toolMsgs.All(m => m.ToolCallId != null), "every tool call gets a tool message with its id (providers require it)");
    Check(lastMsgs.Any(m => m.Role == "assistant" && m.ToolCalls is { Count: 2 }), "the assistant turn keeps its tool_calls in history");
    Check(counts.All(c => c == tools.Count), "tools are offered on every normal round");
    Check(live.Any(l => l.Contains("Reading app/Controllers/ProductController.php")), "progress lines are specific", string.Join(" | ", live));
    Check(!lastMsgs.Any(m => (m.Content ?? "").Contains("hunter2secret")), "no tool result handed to the model contains the password literal");
}

{   // a model that never stops asking: the last round must remove the tools and demand an answer
    var seen = new List<IReadOnlyList<ChatMessage>>(); var counts = new List<int>();
    var n = 0;
    var model = Script(seen, counts, msgs => ++n < 100 && msgs.Count > 0 && counts.Count < AiAgent.MaxModelCalls
        ? Use(Call("read_log", new { log = "nginx", lines = 10 + counts.Count }))
        : Say("Best guess: the nginx log shows a fatal error. Unconfirmed."));
    var res = await AiAgent.RunAsync(req, model, null, CancellationToken.None);
    Check(res.HitLimit && res.ModelCalls == AiAgent.MaxModelCalls, "a run that keeps looking stops at the cap and says so", $"{res.ModelCalls} calls, hit={res.HitLimit}");
    Check(counts[^1] == 0 && counts.Take(counts.Count - 1).All(c => c > 0), "the final round offers no tools");
    Check(seen[^1][^1].Role == "user" && seen[^1][^1].Content!.Contains("Answer now"), "and tells the model to answer now");
}

{   // history stays inside its budget
    var big = new string('x', 4900);
    var msgs = new List<ChatMessage> { ChatMessage.System("s"), ChatMessage.User("u") };
    for (var i = 0; i < 9; i++) { var c = new ToolCall("c" + i, "t", "{}"); msgs.Add(ChatMessage.Assistant(null, new[] { c })); msgs.Add(ChatMessage.Tool(c, big)); }
    AiAgent.Shrink(msgs);
    var total = msgs.Sum(m => m.Content?.Length ?? 0);
    Check(total <= AiAgent.HistoryBudgetChars + 400, "old tool results are shortened once the history is long", total.ToString());
    Check(msgs[^1].Content!.Length == big.Length, "the newest result is kept whole");
}

{   // bad arguments reach the model as text, not as a crash
    var seen = new List<IReadOnlyList<ChatMessage>>(); var counts = new List<int>();
    var model = Script(seen, counts,
        _ => Use(new ToolCall("a1", "read_file", "{not json")),
        _ => Use(Call("read_file", new { site = "shop", path = "../outside.txt" })),
        _ => Say("done"));
    var res = await AiAgent.RunAsync(req, model, null, CancellationToken.None);
    Check(res.Steps[0].Failed && res.Steps[0].Result.Contains("not a valid JSON"), "malformed arguments are reported back");
    Check(res.Steps[1].Failed && res.Steps[1].Result.Contains("outside"), "a refused path is reported back");
}

// ------------------------------------------------------------------ the wire format, against a local fake provider
Console.WriteLine("Provider wire format");
{
    var port = FreePort();
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    var bodies = new List<string>();
    var replies = new Queue<(int, string)>();
    replies.Enqueue((200, """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"","type":"function","function":{"name":"read_log","arguments":{"log":"nginx"}}}]}}]}"""));
    replies.Enqueue((200, """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"<think>hmm</think>All good."}}]}"""));
    replies.Enqueue((400, """{"error":{"message":"This model does not support tools or function calling"}}"""));
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); } catch { break; }
            using var sr = new StreamReader(c.Request.InputStream);
            bodies.Add(await sr.ReadToEndAsync());
            var (code, body) = replies.Dequeue();
            var bytes = Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = code; c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
        }
    });

    var client = new AiClient();
    var call = new AiCall("Local", $"http://127.0.0.1:{port}/v1", "m", null);
    var spec = new ToolSpec("read_log", "d", new { type = "object", properties = new { log = new { type = "string" } } });
    var t1 = await client.ChatWithToolsAsync(call, new[] { ChatMessage.System("s"), ChatMessage.User("q") }, new[] { spec }, 500, TimeSpan.FromSeconds(10), default);
    Check(t1.ToolCalls.Count == 1 && t1.ToolCalls[0].Name == "read_log" && t1.ToolCalls[0].Id.Length == 9 && t1.ToolCalls[0].ArgumentsJson.Contains("nginx"),
        "parses tool_calls; fills a missing id (9 chars); accepts arguments sent as an object", JsonSerializer.Serialize(t1.ToolCalls));
    var sent = JsonDocument.Parse(bodies[0]).RootElement;
    Check(sent.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString() == "read_log" && sent.GetProperty("tool_choice").GetString() == "auto", "request carries tools and tool_choice");

    var hist = new List<ChatMessage> { ChatMessage.System("s"), ChatMessage.User("q"), ChatMessage.Assistant(null, t1.ToolCalls), ChatMessage.Tool(t1.ToolCalls[0], "log text") };
    var t2 = await client.ChatWithToolsAsync(call, hist, Array.Empty<ToolSpec>(), 500, TimeSpan.FromSeconds(10), default);
    Check(t2.Text == "All good." && t2.ToolCalls.Count == 0, "plain answer parsed, <think> removed");
    var sent2 = JsonDocument.Parse(bodies[1]).RootElement;
    var msgsJson = sent2.GetProperty("messages");
    Check(!sent2.TryGetProperty("tools", out _), "no tools field when none are offered (final round)");
    Check(msgsJson[2].GetProperty("role").GetString() == "assistant" && msgsJson[2].GetProperty("content").ValueKind == JsonValueKind.Null
        && msgsJson[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").ValueKind == JsonValueKind.String, "assistant tool_calls echoed with string arguments and null content");
    Check(msgsJson[3].GetProperty("role").GetString() == "tool" && msgsJson[3].GetProperty("tool_call_id").GetString() == t1.ToolCalls[0].Id, "tool result sent with tool_call_id");

    try { await client.ChatWithToolsAsync(call, hist, new[] { spec }, 500, TimeSpan.FromSeconds(10), default); Check(false, "tools-unsupported is detected"); }
    catch (AiException e) { Check(e.ToolsUnsupported && e.Message.Contains("doesn't support tool calling"), "a 400 about tools becomes a clear 'pick another model' message", e.Message); }
    listener.Stop();
}

// ------------------------------------------------------------------ the service: backup provider takes over mid-run
Console.WriteLine("Service");
{
    var port1 = FreePort(); var port2 = FreePort();
    var good = StartFake(port2, new Queue<(int, string)>(new[] { (200, """{"choices":[{"finish_reason":"stop","message":{"content":"From the backup."}}]}""") }));
    var bad = StartFake(port1, new Queue<(int, string)>(new[] { (503, "{}") }));
    var settings = new AiSettings { Enabled = true, Primary = new AiEndpoint { Provider = "custom", BaseUrl = $"http://127.0.0.1:{port1}/v1", Model = "a" },
        Fallback = new AiEndpoint { Provider = "custom", BaseUrl = $"http://127.0.0.1:{port2}/v1", Model = "b" } };
    var svc = new AiService(settings, () => { }, new AiKeyStore(Path.Combine(tmp, "keys.json"), new NoCrypt()));
    var r = await svc.RunAgentAsync(new AgentRequest("q", tools, redactor.Apply, "sys"), null, default);
    Check(r.Answer == "From the backup." && r.UsedFallback && r.FallbackNote!.Contains("couldn't answer"), "a failing primary hands the run to the backup", r.FallbackNote);
    bad.Stop(); good.Stop();
    settings.Enabled = false;
    try { await svc.RunAgentAsync(new AgentRequest("q", tools, redactor.Apply, "sys"), null, default); Check(false, "off means off"); }
    catch (AiException e) { Check(e.Message.Contains("turned off"), "nothing runs while AI help is off"); }
}

Console.WriteLine("Redactor");
{
    var rd = new AiRedactor();
    Check(!rd.Apply("'password' => 'hunter2',").Contains("hunter2"), "PHP array '=>' password is now redacted in normal mode");
    Check(rd.Apply("$token = $request->get('token');", sourceCode: true).Contains("$request->get('token')"), "code mode leaves non-literal assignments alone");
    Check(!rd.Apply("define('DB_PASS', 'abc123xyz');", sourceCode: true).Contains("abc123xyz"), "code mode catches define('DB_PASS', 'x')");
    Check(!rd.Apply("DB_PASS=abc123xyz").Contains("abc123xyz"), "normal mode still catches .env style");
}

try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static int FreePort() { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

static HttpListener StartFake(int port, Queue<(int, string)> replies)
{
    var l = new HttpListener(); l.Prefixes.Add($"http://127.0.0.1:{port}/"); l.Start();
    _ = Task.Run(async () =>
    {
        while (l.IsListening)
        {
            HttpListenerContext c;
            try { c = await l.GetContextAsync(); } catch { break; }
            using (var sr = new StreamReader(c.Request.InputStream)) await sr.ReadToEndAsync();
            var (code, body) = replies.Count > 0 ? replies.Dequeue() : (500, "{}");
            var bytes = Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = code; c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
        }
    });
    return l;
}

sealed class NoCrypt : ISecretProtector
{
    public byte[] Protect(byte[] plain) => plain;
    public byte[] Unprotect(byte[] p) => p;
}

sealed class FakeCtx(ProfileStore store, string root) : IDevContext
{
    public bool Enabled = true;
    public IReadOnlyList<DevSite> Sites => new[] { new DevSite("shop", "shop.test", root, "", "8.3") };
    public IReadOnlyList<string> LogIds => new[] { "nginx", "mariadb", "php-8.3" };
    public bool ProfilingEnabled => Enabled;
    public string ReadLog(string id, int lines) => id == "nginx"
        ? "2026/11/04 10:00:01 [notice] start\n2026/11/04 10:00:02 [error] FastCGI sent in stderr: PHP Fatal error: Call to undefined method Foo::bar() in /app/X.php:12\n"
        : "(nothing logged yet)";
    public Task<string> CheckSiteAsync(string name, CancellationToken ct) => Task.FromResult("Site: shop.test OK");
    public ProfileSnapshot Profile() => store.Snapshot(Enabled, 500, 5000);
    public Task<DevProbe> ProbeAsync(DevSite site, string path, CancellationToken ct) =>
        Task.FromResult(new DevProbe(200, "OK", 12, new[] { "Content-Type: text/html" }, "Product page", null));
}
