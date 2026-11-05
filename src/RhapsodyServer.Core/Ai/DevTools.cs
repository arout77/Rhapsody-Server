using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RhapsodyServer.Core.Profiling;

namespace RhapsodyServer.Core.Ai;

public sealed record DevSite(string Name, string Domain, string Root, string PublicDir, string Php, bool Https = true);

/// <summary>What loading one page of a site returned. <c>Error</c> is set when nothing answered at all.</summary>
public sealed record DevProbe(int? Status, string? Reason, double Ms, IReadOnlyList<string> Headers, string Body, string? Error);

/// <summary>
/// What the assistant's tools can see of the running stack. The real implementation wraps <c>Stack</c>; tests hand in a fake,
/// so every tool below can be exercised without Windows, nginx or PHP.
/// </summary>
public interface IDevContext
{
    IReadOnlyList<DevSite> Sites { get; }
    /// <summary>Ids the log viewer knows: the web server, mariadb, redis, mailpit, php-8.3...</summary>
    IReadOnlyList<string> LogIds { get; }
    bool ProfilingEnabled { get; }
    string ReadLog(string id, int lines);
    Task<string> CheckSiteAsync(string name, CancellationToken ct);
    /// <summary>Everything the profiler still holds, newest first.</summary>
    ProfileSnapshot Profile();
    /// <summary>A GET to the local web server for this site. Never leaves the machine.</summary>
    Task<DevProbe> ProbeAsync(DevSite site, string pathAndQuery, CancellationToken ct);
}

/// <summary>
/// The general-purpose toolbox: logs, the profiler, the site's own files, and (if the person allows it) loading a page.
/// Nothing here depends on which framework a site uses. Every tool is read-only, and none can reach beyond this computer.
/// </summary>
public static class DevTools
{
    // ------------------------------------------------------------------ argument helpers
    static string? Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())?.Trim() : null;

    static int Int(JsonElement a, string name, int def, int min, int max)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Clamp(d, min, max);
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return (int)Math.Clamp(s, min, max);
        }
        return def;
    }

    static object Schema(object properties, params string[] required) => new { type = "object", properties, required };
    static object P(string description, string type = "string") => new { type, description };

    static DevSite SiteOf(IDevContext ctx, string? name)
    {
        if (ctx.Sites.Count == 0) throw new ArgumentException("There are no sites yet.");
        if (string.IsNullOrWhiteSpace(name))
        {
            if (ctx.Sites.Count == 1) return ctx.Sites[0];
            throw new ArgumentException("Say which site. The sites are: " + string.Join(", ", ctx.Sites.Select(s => s.Name)) + ".");
        }
        var n = name.Trim();
        return ctx.Sites.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase) || s.Domain.Equals(n, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There is no site called \"{n}\". The sites are: {string.Join(", ", ctx.Sites.Select(s => s.Name))}.");
    }

    static string[] SiteNames(IDevContext ctx) => ctx.Sites.Select(s => s.Name).ToArray();

    static object SiteParam(IDevContext ctx, string description, bool required = true)
    {
        var names = SiteNames(ctx);
        return names.Length > 0 ? new { type = "string", description, @enum = names } : (object)new { type = "string", description };
    }

    // ------------------------------------------------------------------ the toolbox
    public static List<AgentTool> Build(IDevContext ctx, AiRedactor redactor, bool allowRequests)
    {
        var tools = new List<AgentTool>
        {
            new()
            {
                Name = "check_site",
                Description = "Health check of one site: is the web server up, are PHP workers and ports open, is the hosts entry and .env there, " +
                    "what does the home page answer right now, and the last lines of the PHP and web server error logs. A good first look when something is broken and you don't yet know why.",
                Parameters = Schema(new { site = SiteParam(ctx, "Site name.") }),
                Label = a => $"Checking {Str(a, "site") ?? "the site"}",
                Run = async (a, ct) => await ctx.CheckSiteAsync(SiteOf(ctx, Str(a, "site")).Name, ct)
            },
            new()
            {
                Name = "recent_requests",
                Description = "Requests the profiler recorded (newest first, or slowest first), each with status, total time, time in PHP, number of SQL queries and time in SQL. " +
                    "Use it to find the request the person means, to spot slow or failing ones, and to get a request number for request_detail. Needs the profiler to be on.",
                Parameters = Schema(new
                {
                    site = SiteParam(ctx, "Only requests to this site.", required: false),
                    uri_contains = P("Only requests whose URL contains this text, e.g. \"/products/123\"."),
                    min_status = P("Only requests with this HTTP status or higher, e.g. 500 for server errors, 400 for any error.", "integer"),
                    min_ms = P("Only requests at least this slow, in milliseconds.", "integer"),
                    sort = new { type = "string", description = "\"newest\" (default) or \"slowest\".", @enum = new[] { "newest", "slowest" } },
                    limit = P("How many to list (default 10, at most 20).", "integer")
                }),
                Label = a => "Looking at recent requests" + (Str(a, "uri_contains") is { Length: > 0 } u ? $" for {u}" : ""),
                Run = (a, ct) => Task.FromResult(RecentRequests(ctx, a))
            },
            new()
            {
                Name = "request_detail",
                Description = "One recorded request in full: timings, every SQL statement it ran, which statements repeat (a sign of N+1 queries), the slowest statements and any SQL errors. " +
                    "Takes the request number from recent_requests.",
                Parameters = Schema(new { id = P("Request number, as shown after # in recent_requests.", "integer") }, "id"),
                Label = a => $"Reading request #{Str(a, "id")}",
                Run = (a, ct) => Task.FromResult(RequestDetail(ctx, a))
            },
            new()
            {
                Name = "slow_queries",
                Description = "SQL the profiler saw across all requests, grouped by shape (values replaced by ?): how often each ran, total and slowest time, errors. Finds the statement that costs the most overall. Needs the profiler to be on.",
                Parameters = Schema(new
                {
                    min_ms = P("Ignore statements whose slowest run is under this many milliseconds (default 20).", "integer"),
                    sort = new { type = "string", description = "\"total\" time (default), \"max\" single run, or \"count\".", @enum = new[] { "total", "max", "count" } },
                    limit = P("How many groups to list (default 8, at most 15).", "integer")
                }),
                Label = _ => "Looking for slow SQL",
                Run = (a, ct) => Task.FromResult(SlowQueries(ctx, a))
            },
            new()
            {
                Name = "read_log",
                Description = "Recent lines of a service log, including the program's own error log (the web server's error.log, PHP's php_error.log). Repeated lines are collapsed with (xN). " +
                    "Use filter to find one thing, such as an exception name, a URL or a table.",
                Parameters = Schema(new
                {
                    log = new { type = "string", description = "Which log.", @enum = ctx.LogIds.ToArray() },
                    lines = P("How many recent lines (default 120, at most 400).", "integer"),
                    filter = P("Only keep lines containing this text (case-insensitive).")
                }, "log"),
                Label = a => $"Reading the {Str(a, "log")} log" + (Str(a, "filter") is { Length: > 0 } f ? $" for \"{f}\"" : ""),
                Run = (a, ct) => Task.FromResult(ReadLog(ctx, a))
            },
            new()
            {
                Name = "list_dir",
                Description = "List a folder inside a site's project (not recursive). Use it to find where code lives.",
                Parameters = Schema(new { site = SiteParam(ctx, "Site name."), path = P("Folder relative to the project root; empty for the root.") }),
                Label = a => $"Listing {(Str(a, "path") is { Length: > 0 } p ? p : "the project folder")}",
                Run = (a, ct) => Task.FromResult(ListDir(ctx, a))
            },
            new()
            {
                Name = "read_file",
                Description = "Read lines of a text file inside a site's project, with line numbers. Use it for the file and line named in a stack trace, a controller, a template, a config file. " +
                    "Secrets in it are hidden. At most 160 lines at a time.",
                Parameters = Schema(new
                {
                    site = SiteParam(ctx, "Site name."),
                    path = P("File path relative to the project root, e.g. app/Controllers/ProductController.php."),
                    start_line = P("First line to show (default 1).", "integer"),
                    end_line = P("Last line to show (default start_line + 99).", "integer")
                }, "path"),
                Label = a => $"Reading {Str(a, "path")}" + (Int(a, "start_line", 0, 0, int.MaxValue) > 0 ? $" from line {Int(a, "start_line", 1, 1, int.MaxValue)}" : ""),
                Redacted = true,
                Run = (a, ct) => Task.FromResult(ReadFile(ctx, redactor, a))
            },
            new()
            {
                Name = "search_files",
                Description = "Find a piece of text (not a pattern) in a site's files: a function name, a route, a table, an error message. Shows file:line and the line. " +
                    "Skips .git, node_modules and vendor unless path points inside them.",
                Parameters = Schema(new
                {
                    site = SiteParam(ctx, "Site name."),
                    text = P("Text to look for (case-insensitive)."),
                    path = P("Only search under this folder (relative to the project root)."),
                    max_results = P("Default 20, at most 40.", "integer")
                }, "text"),
                Label = a => $"Searching the code for \"{Str(a, "text")}\"",
                Redacted = true,
                Run = (a, ct) => Task.FromResult(SearchFiles(ctx, redactor, a, ct))
            }
        };

        if (allowRequests)
            tools.Add(new AgentTool
            {
                Name = "load_page",
                Description = "Load one page of a site the way a browser would (a GET through the local web server) and report the status, time, headers and the text of the page. " +
                    "Use it to reproduce an error or a slow page, or to read an error page's message. It cannot log in, submit forms or leave this computer. Loading it records it in the profiler.",
                Parameters = Schema(new
                {
                    site = SiteParam(ctx, "Site name."),
                    path = P("Path and query starting with /, e.g. /products/123?sort=price.")
                }, "path"),
                Label = a => $"Loading {Str(a, "path")}",
                Run = async (a, ct) => await LoadPage(ctx, a, ct)
            });

        return tools;
    }

    // ------------------------------------------------------------------ profiler tools
    const string ProfilerOff = "The profiler is off, so there is no timing or SQL data. Tell the person to turn it on from the Profiler page (it restarts MariaDB and the web server), then load the page again.";

    static string HostOf(RequestRecord r) => r.Host.Split(':')[0];

    static string RequestLine(RequestView v)
    {
        var r = v.Request;
        var php = r.UpstreamMs is { } u ? $" (PHP {Ms(u)})" : "";
        var sql = v.Queries.Count > 0 ? $"  SQL {v.Queries.Count} quer{(v.Queries.Count == 1 ? "y" : "ies")}, {Ms(v.SqlMs)}" : "  no SQL";
        return $"#{r.Id}  {r.StartedAt.ToLocalTime():HH:mm:ss}  {r.Method} {r.Host}{r.Uri}  -> {r.Status}  {Ms(r.DurationMs)}{php}{sql}";
    }

    static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:0.##}s" : $"{ms:0}ms";

    static string RecentRequests(IDevContext ctx, JsonElement a)
    {
        if (!ctx.ProfilingEnabled) return ProfilerOff;
        var snap = ctx.Profile();
        IEnumerable<RequestView> q = snap.Requests;
        string? why = null;

        if (Str(a, "site") is { Length: > 0 } sn)
        {
            var site = SiteOf(ctx, sn);
            q = q.Where(v => HostOf(v.Request).Equals(site.Domain, StringComparison.OrdinalIgnoreCase));
            why += $" for {site.Domain}";
        }
        if (Str(a, "uri_contains") is { Length: > 0 } uc) { q = q.Where(v => v.Request.Uri.Contains(uc, StringComparison.OrdinalIgnoreCase)); why += $" with \"{uc}\" in the URL"; }
        var minStatus = Int(a, "min_status", 0, 0, 999);
        if (minStatus > 0) { q = q.Where(v => v.Request.Status >= minStatus); why += $" with status {minStatus}+"; }
        var minMs = Int(a, "min_ms", 0, 0, int.MaxValue);
        if (minMs > 0) { q = q.Where(v => v.Request.DurationMs >= minMs); why += $" taking {minMs}ms+"; }

        var list = q.ToList();
        var slowest = (Str(a, "sort") ?? "").Equals("slowest", StringComparison.OrdinalIgnoreCase);
        if (slowest) list = list.OrderByDescending(v => v.Request.DurationMs).ToList();
        var limit = Int(a, "limit", 10, 1, 20);

        if (list.Count == 0)
            return snap.Requests.Length == 0
                ? "The profiler is on but has recorded no requests yet. Ask the person to load the page in the browser, then look again."
                : $"No recorded request{why}. The profiler holds {snap.Requests.Length} request(s) in total; try a looser filter.";

        var sb = new StringBuilder();
        sb.AppendLine($"{list.Count} request(s){why}; showing {Math.Min(limit, list.Count)} {(slowest ? "slowest first" : "newest first")}. (Time shown is local. Queries are matched to requests by time.)");
        foreach (var v in list.Take(limit)) sb.AppendLine(RequestLine(v));
        return sb.ToString().TrimEnd();
    }

    static readonly Regex SqlStr = new(@"'(?:[^'\\]|\\.|'')*'|""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);
    static readonly Regex SqlNum = new(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled);
    static readonly Regex SqlList = new(@"\(\s*\?(?:\s*,\s*\?)+\s*\)", RegexOptions.Compiled);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The statement with its values replaced by ?, so 40 lookups by different ids count as one repeated query.</summary>
    internal static string Shape(string sql)
    {
        var s = SqlStr.Replace(sql, "?");
        s = SqlNum.Replace(s, "?");
        s = SqlList.Replace(s, "(?)");
        return Spaces.Replace(s, " ").Trim();
    }

    static string ClipSql(string sql, int max = 300)
    {
        var s = Spaces.Replace(sql, " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    static string RequestDetail(IDevContext ctx, JsonElement a)
    {
        if (!ctx.ProfilingEnabled) return ProfilerOff;
        var id = Int(a, "id", -1, -1, int.MaxValue);
        if (id < 0) throw new ArgumentException("Give the request number (the digits after # in recent_requests).");
        var v = ctx.Profile().Requests.FirstOrDefault(x => x.Request.Id == id)
            ?? throw new ArgumentException($"No recorded request #{id}. It may have scrolled out of the profiler's history; run recent_requests again.");
        var r = v.Request;

        var sb = new StringBuilder();
        sb.AppendLine(RequestLine(v));
        sb.AppendLine($"Response size {r.Bytes:N0} bytes." + (r.UpstreamMs is { } u ? $" PHP took {Ms(u)}; the rest ({Ms(Math.Max(0, r.DurationMs - u))}) was the web server and network." : ""));
        if (v.Queries.Count == 0) { sb.AppendLine("No SQL ran during this request."); return sb.ToString().TrimEnd(); }

        var pct = r.DurationMs > 0 ? $" ({Math.Min(100, v.SqlMs / r.DurationMs * 100):0}% of the request)" : "";
        sb.AppendLine($"SQL: {v.Queries.Count} queries, {Ms(v.SqlMs)} in total{pct}.");

        var groups = v.Queries.GroupBy(x => Shape(x.Sql)).Select(g => new { Shape = g.Key, Count = g.Count(), Total = g.Sum(x => x.TotalMs), Max = g.Max(x => x.TotalMs), Example = g.First().Sql })
            .OrderByDescending(g => g.Total).ToList();
        var repeated = groups.Where(g => g.Count >= 3).ToList();
        if (repeated.Count > 0)
        {
            sb.AppendLine("\nRepeated statements (same shape, different values; many of these usually means a query inside a loop, an N+1):");
            foreach (var g in repeated.Take(5)) sb.AppendLine($"  x{g.Count}  {Ms(g.Total)} total, slowest {Ms(g.Max)}  {ClipSql(g.Shape, 220)}");
        }
        sb.AppendLine("\nCostliest statements:");
        foreach (var g in groups.Take(5)) sb.AppendLine($"  {Ms(g.Total)} total, x{g.Count}, slowest {Ms(g.Max)}  {ClipSql(g.Example, 260)}");

        var errs = v.Queries.Where(x => !string.IsNullOrEmpty(x.Error)).ToList();
        if (errs.Count > 0)
        {
            sb.AppendLine("\nSQL errors:");
            foreach (var e in errs.Take(5)) sb.AppendLine($"  {ClipSql(e.Sql, 180)}\n    -> {ClipSql(e.Error!, 200)}");
        }
        sb.AppendLine("\n(Queries are matched to this request by time, so a request that overlapped another may show some of its queries.)");
        return sb.ToString().TrimEnd();
    }

    static string SlowQueries(IDevContext ctx, JsonElement a)
    {
        if (!ctx.ProfilingEnabled) return ProfilerOff;
        var snap = ctx.Profile();
        if (snap.Queries.Length == 0) return "The profiler is on but has recorded no SQL yet.";
        var minMs = Int(a, "min_ms", 20, 0, int.MaxValue);
        var limit = Int(a, "limit", 8, 1, 15);
        var sort = (Str(a, "sort") ?? "total").ToLowerInvariant();

        var groups = snap.Queries.GroupBy(x => Shape(x.Sql))
            .Select(g => new { Shape = g.Key, Count = g.Count(), Total = g.Sum(x => x.TotalMs), Max = g.Max(x => x.TotalMs), Errors = g.Count(x => !string.IsNullOrEmpty(x.Error)), Example = g.OrderByDescending(x => x.TotalMs).First() })
            .Where(g => g.Max >= minMs || g.Errors > 0);
        groups = sort switch { "max" => groups.OrderByDescending(g => g.Max), "count" => groups.OrderByDescending(g => g.Count), _ => groups.OrderByDescending(g => g.Total) };
        var list = groups.Take(limit).ToList();
        if (list.Count == 0) return $"No statement ran slower than {minMs}ms among the {snap.Queries.Length} recorded queries.";

        var sb = new StringBuilder($"Out of {snap.Queries.Length} recorded queries, by {sort}:\n");
        foreach (var g in list)
        {
            sb.AppendLine($"  {Ms(g.Total)} total, x{g.Count}, slowest {Ms(g.Max)}{(g.Errors > 0 ? $", {g.Errors} error(s)" : "")}  {ClipSql(g.Shape, 260)}");
            if (g.Example.Error is { Length: > 0 } e) sb.AppendLine($"    error: {ClipSql(e, 200)}");
        }
        sb.AppendLine("(Values are shown as ?. Process names: " + string.Join(", ", snap.Queries.Select(q => q.Process).Where(p => !string.IsNullOrEmpty(p)).Distinct().Take(4)) + ")");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ logs
    static string ReadLog(IDevContext ctx, JsonElement a)
    {
        var id = (Str(a, "log") ?? "").ToLowerInvariant();
        if (!ctx.LogIds.Contains(id)) throw new ArgumentException($"There is no log called \"{id}\". The logs are: {string.Join(", ", ctx.LogIds)}.");
        var lines = Int(a, "lines", 120, 10, 400);
        var filter = Str(a, "filter");
        var text = ctx.ReadLog(id, string.IsNullOrEmpty(filter) ? lines : 800);
        if (text.StartsWith("(nothing logged yet)", StringComparison.Ordinal)) return $"The {id} log is empty.";

        if (!string.IsNullOrEmpty(filter))
        {
            var hit = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).TakeLast(lines).ToList();
            if (hit.Count == 0) return $"No line in the recent {id} log contains \"{filter}\".";
            text = $"{hit.Count} matching line(s):\n" + string.Join('\n', hit);
        }
        return LogCondenser.Condense(text, AiAgent.MaxResultChars - 200);
    }

    // ------------------------------------------------------------------ files
    static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".php", ".phtml", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".twig", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".py", ".rb", ".cs", ".java", ".go", ".rs", ".sql" };

    static readonly HashSet<string> BlockedExt = new(StringComparer.OrdinalIgnoreCase)
        { ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".kdbx", ".ppk" };

    static readonly HashSet<string> BlockedNames = new(StringComparer.OrdinalIgnoreCase)
        { "id_rsa", "id_ed25519", "id_ecdsa", ".htpasswd", "auth.json" };

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "vendor", ".idea", ".vscode" };

    const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Turns what the model asked for into a real path, or refuses. Nothing outside the site's own folder, no ".git" (it can hold
    /// tokens), no key files, and no links: a junction or symlink inside a project could point anywhere.
    /// </summary>
    internal static string Resolve(string root, string? rel)
    {
        var r = (rel ?? "").Trim().Replace('\\', '/').TrimStart('/');
        // A colon means a drive letter or, on Windows, an alternate data stream (file.txt:hidden); neither belongs in a project path.
        if (r.Contains('\0') || r.Contains(':'))
            throw new ArgumentException("Give a path inside the project, like app/Controllers/HomeController.php.");

        string rootFull, full;
        try
        {
            rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            full = Path.GetFullPath(Path.Combine(rootFull, r));
        }
        catch (Exception ex) when (ex is NotSupportedException or PathTooLongException or ArgumentException)
        {
            throw new ArgumentException("That isn't a usable path.");
        }

        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.Equals(rootFull, cmp) && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, cmp))
            throw new ArgumentException("That path is outside the site's project folder.");

        var parts = Path.GetRelativePath(rootFull, full).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != ".").ToArray();
        var cur = rootFull;
        foreach (var part in parts)
        {
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The .git folder is off limits.");
            cur = Path.Combine(cur, part);
            if ((File.Exists(cur) || Directory.Exists(cur)) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("That path goes through a link (shortcut, junction or symlink), which isn't followed.");
        }
        var name = parts.Length > 0 ? parts[^1] : "";
        if (BlockedNames.Contains(name) || BlockedExt.Contains(Path.GetExtension(name)))
            throw new ArgumentException("That file can hold keys or passwords, so it isn't readable here.");
        return full;
    }

    static string Rel(string root, string full) =>
        Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/');

    static string ListDir(IDevContext ctx, JsonElement a)
    {
        var site = SiteOf(ctx, Str(a, "site"));
        var dir = Resolve(site.Root, Str(a, "path"));
        if (!Directory.Exists(dir)) throw new ArgumentException($"There is no folder \"{Str(a, "path")}\" in {site.Name}.");
        var opts = new EnumerationOptions { AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false, IgnoreInaccessible = true };
        var dirs = Directory.EnumerateDirectories(dir, "*", opts).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var files = Directory.EnumerateFiles(dir, "*", opts).Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder($"{site.Name}/{Rel(site.Root, dir).TrimStart('.').TrimStart('/')}  ({dirs.Count} folders, {files.Count} files)\n");
        foreach (var d in dirs.Take(60)) sb.AppendLine($"  {d}/" + (SkipDirs.Contains(d!) ? "   (large or generated; not searched by default)" : ""));
        foreach (var f in files.Take(60)) sb.AppendLine($"  {f.Name}   {(f.Length < 1024 ? f.Length + " B" : f.Length < 1024 * 1024 ? $"{f.Length / 1024.0:0.#} KB" : $"{f.Length / 1048576.0:0.#} MB")}");
        if (dirs.Count > 60 || files.Count > 60) sb.AppendLine("  …(more not shown)");
        return sb.ToString().TrimEnd();
    }

    static bool LooksBinary(byte[] head, int n)
    {
        for (var i = 0; i < n; i++) if (head[i] == 0) return true;
        return false;
    }

    static string ReadFile(IDevContext ctx, AiRedactor redactor, JsonElement a)
    {
        var site = SiteOf(ctx, Str(a, "site"));
        var path = Str(a, "path");
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Give the file path.");
        var full = Resolve(site.Root, path);
        if (Directory.Exists(full)) throw new ArgumentException($"\"{path}\" is a folder; use list_dir.");
        if (!File.Exists(full)) throw new ArgumentException($"There is no file \"{path}\" in {site.Name}. Use list_dir or search_files to find it.");
        var len = new FileInfo(full).Length;
        if (len > MaxFileBytes) throw new ArgumentException($"That file is {len / 1048576.0:0.#} MB, too big to read. Use search_files to find the part you need.");

        byte[] bytes;
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            var read = 0;
            while (read < bytes.Length) { var n = fs.Read(bytes, read, bytes.Length - read); if (n == 0) break; read += n; }
        }
        if (LooksBinary(bytes, Math.Min(bytes.Length, 4000))) throw new ArgumentException("That is a binary file, not text.");

        var all = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n');
        var start = Int(a, "start_line", 1, 1, Math.Max(1, all.Length));
        var end = Int(a, "end_line", start + 99, start, int.MaxValue);
        end = Math.Min(Math.Min(end, start + 159), all.Length);
        var sourceCode = CodeExt.Contains(Path.GetExtension(full));

        var body = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var line = all[i - 1];
            if (line.Length > 300) line = line[..300] + "…";
            body.Append(i.ToString().PadLeft(5)).Append("| ").AppendLine(line);
        }
        // Scrub the whole block at once: a multi-line secret (a private key, a quoted value) has to be seen whole.
        var text = redactor.Apply(body.ToString(), sourceCode);
        var head = $"{Rel(site.Root, full)}  (lines {start}-{end} of {all.Length})\n";
        return AiAgent.Clip(head + text.TrimEnd(), AiAgent.MaxResultChars);
    }

    static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".svgz", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".zip", ".gz", ".7z", ".rar", ".exe", ".dll", ".pdf", ".mp3", ".mp4", ".mov", ".sqlite", ".db", ".map" };

    static string SearchFiles(IDevContext ctx, AiRedactor redactor, JsonElement a, CancellationToken ct)
    {
        var site = SiteOf(ctx, Str(a, "site"));
        var needle = Str(a, "text");
        if (string.IsNullOrEmpty(needle) || needle.Length < 2) throw new ArgumentException("Give at least two characters of text to look for.");
        var startDir = Resolve(site.Root, Str(a, "path"));
        if (!Directory.Exists(startDir)) throw new ArgumentException($"There is no folder \"{Str(a, "path")}\" in {site.Name}.");
        var max = Int(a, "max_results", 20, 1, 40);

        var opts = new EnumerationOptions { AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint, RecurseSubdirectories = false, IgnoreInaccessible = true };
        var sw = Stopwatch.StartNew();
        var pending = new Stack<string>();
        pending.Push(startDir);
        var hits = new List<string>();
        int scanned = 0; var stopped = false;

        while (pending.Count > 0 && !stopped)
        {
            var dir = pending.Pop();
            foreach (var d in Directory.EnumerateDirectories(dir, "*", opts))
                if (!SkipDirs.Contains(Path.GetFileName(d))) pending.Push(d);

            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (sw.ElapsedMilliseconds > 4000 || scanned >= 4000) { stopped = true; break; }
                var name = Path.GetFileName(f);
                var ext = Path.GetExtension(f);
                if (SkipExt.Contains(ext) || BlockedExt.Contains(ext) || BlockedNames.Contains(name)) continue;
                var info = new FileInfo(f);
                if (info.Length > 1024 * 1024) continue;
                scanned++;
                string[] lines;
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var head = new byte[Math.Min(4000, (int)fs.Length)];
                    var n = fs.Read(head, 0, head.Length);
                    if (LooksBinary(head, n)) continue;
                    fs.Seek(0, SeekOrigin.Begin);
                    using var sr = new StreamReader(fs);
                    lines = sr.ReadToEnd().Replace("\r\n", "\n").Split('\n');
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var code = CodeExt.Contains(ext);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var snippet = lines[i].Trim();
                    if (snippet.Length > 180) snippet = snippet[..180] + "…";
                    hits.Add($"{Rel(site.Root, f)}:{i + 1}: {redactor.Apply(snippet, code)}");
                    if (hits.Count >= max) { stopped = true; break; }
                }
                if (stopped) break;
            }
        }

        if (hits.Count == 0) return $"\"{needle}\" was not found in {scanned} file(s) searched{(stopped ? " (stopped early; narrow with path)" : "")}.";
        var sb = new StringBuilder($"{hits.Count} match(es) for \"{needle}\" ({scanned} files searched{(stopped && hits.Count < max ? ", stopped early" : "")}):\n");
        foreach (var h in hits) sb.AppendLine(h);
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ load a page
    static async Task<string> LoadPage(IDevContext ctx, JsonElement a, CancellationToken ct)
    {
        var site = SiteOf(ctx, Str(a, "site"));
        var path = Str(a, "path") ?? "";
        if (!path.StartsWith('/') || path.StartsWith("//") || path.Length > 500 || path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("The path must start with a single / and have no spaces, like /products/123?sort=price.");
        var p = await ctx.ProbeAsync(site, path, ct);
        var sb = new StringBuilder();
        if (p.Error != null) return $"GET {site.Domain}{path} got no answer after {Ms(p.Ms)}: {p.Error}";
        sb.AppendLine($"GET {site.Domain}{path} -> {p.Status} {p.Reason} in {Ms(p.Ms)}");
        if (p.Headers.Count > 0) sb.AppendLine("Headers: " + string.Join("; ", p.Headers));
        sb.AppendLine(p.Body.Length > 0 ? "Page text: " + p.Body : "(empty body)");
        return sb.ToString().TrimEnd();
    }
}
