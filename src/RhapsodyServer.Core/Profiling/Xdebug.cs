using System.Globalization;
using System.Text.RegularExpressions;

namespace RhapsodyServer.Core.Profiling;

public sealed record XdebugBuild(string Branch, string Version, string Url, string FileName);

/// <summary>Finds the Windows Xdebug DLL for a PHP branch on xdebug.org. Parsing is pure so it is tested without a network.</summary>
public static class XdebugResolver
{
    public const string ListingUrl = "https://xdebug.org/download/historical";
    public const string FilesBase = "https://xdebug.org/files/";

    /// <summary>Xdebug 3 (profiler trigger, output_dir, mode) needs PHP 7.4 or newer here. PHP 7.1 would need Xdebug 2 and different settings.</summary>
    public static bool Supports(string branch) => VersionUtil.Compare(branch, "7.4") >= 0;

    // php_xdebug-3.5.3-8.0-nts-vs16-x86_64.dll   and   php_xdebug-3.1.6-7.4-vc15-nts-x86_64.dll
    static Regex Rx(string branch) => new(
        $@"php_xdebug-(?<ver>\d+\.\d+\.\d+)(?<pre>(?:alpha|beta|RC)\d*)?-{Regex.Escape(branch)}-(?:nts-(?:vs|vc)\d+|(?:vs|vc)\d+-nts)-x86_64\.dll",
        RegexOptions.IgnoreCase);

    /// <summary>The newest stable (not alpha, beta or release candidate) NTS x64 build for the branch, or null.</summary>
    public static XdebugBuild? FromListing(string html, string branch)
    {
        var best = Rx(branch).Matches(html)
            .Where(m => !m.Groups["pre"].Success)
            .Select(m => (File: m.Value, Ver: Version.Parse(m.Groups["ver"].Value)))
            .OrderByDescending(x => x.Ver)
            .FirstOrDefault();
        return best.File == null ? null : new XdebugBuild(branch, best.Ver.ToString(), FilesBase + best.File, best.File);
    }

    /// <summary>
    /// One GET on its own connection. xdebug.org's very large listing page can send more bytes than its headers declare;
    /// on a reused keep-alive connection those leftovers are read as the next response's status line ("invalid status line").
    /// Closing the connection after each request avoids that, and one retry covers a flaky read.
    /// </summary>
    public static async Task<byte[]> FetchAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var rq = new HttpRequestMessage(HttpMethod.Get, url);
                rq.Headers.ConnectionClose = true;
                using var res = await http.SendAsync(rq, HttpCompletionOption.ResponseContentRead, ct);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsByteArrayAsync(ct);
            }
            catch (HttpRequestException) when (attempt < 2) { await Task.Delay(500, ct); }
        }
    }

    public static async Task<XdebugBuild> ResolveAsync(HttpClient http, string branch, CancellationToken ct = default)
    {
        if (!Supports(branch)) throw new InvalidOperationException($"Xdebug profiling needs PHP 7.4 or newer (this is PHP {branch}).");
        string html;
        try { html = System.Text.Encoding.UTF8.GetString(await FetchAsync(http, ListingUrl, ct)); }
        catch (HttpRequestException ex) { throw new InvalidOperationException("xdebug.org could not be reached: " + ex.Message); }
        return FromListing(html, branch) ?? throw new InvalidOperationException($"xdebug.org lists no Xdebug build for PHP {branch}.");
    }
}

public sealed record XdebugFunction(string Name, string File, long Calls, double SelfMs, double InclusiveMs);
/// <param name="TotalMs">Time the profiler saw in total (the {main} script and everything it called).</param>
/// <param name="Functions">Costliest functions first, by self time.</param>
public sealed record XdebugSummary(string File, string Command, double TotalMs, int FunctionCount, XdebugFunction[] Functions);

/// <summary>
/// Reads an Xdebug profiler file (callgrind / cachegrind format) and totals the time per function.
/// Streams the file, so a profile of a big framework request doesn't need to fit in memory twice.
/// </summary>
public static class CachegrindParser
{
    public static XdebugSummary Parse(string path, int top = 40)
    {
        using var rd = new StreamReader(path);
        return Parse(rd, path, top);
    }

    sealed class Agg { public string Name = ""; public string File = ""; public long Calls; public double Self; public double Incl; }

    public static XdebugSummary Parse(TextReader rd, string source, int top = 40)
    {
        var files = new Dictionary<string, string>();
        var fns = new Dictionary<string, string>();
        var agg = new Dictionary<(string, string), Agg>();
        string cmd = "";
        int timeCol = 0;
        double unitMs = 1.0 / 100_000;                 // Xdebug counts time in units of 10 ns
        string curFile = "", curFn = "";
        string callFile = "", callFn = "";
        bool afterCalls = false;
        long callCount = 0;
        double total = 0;

        // "(12) name" defines id 12; a bare "(12)" refers back to it.
        static string Resolve(Dictionary<string, string> table, string rest)
        {
            rest = rest.Trim();
            if (rest.Length > 0 && rest[0] == '(')
            {
                var close = rest.IndexOf(')');
                if (close > 0)
                {
                    var id = rest[1..close];
                    var name = rest[(close + 1)..].Trim();
                    if (name.Length > 0) { table[id] = name; return name; }
                    return table.TryGetValue(id, out var known) ? known : "(" + id + ")";
                }
            }
            return rest;
        }

        Agg Get(string fn, string file)
        {
            if (!agg.TryGetValue((fn, file), out var a)) agg[(fn, file)] = a = new Agg { Name = fn, File = file };
            return a;
        }

        string? line;
        while ((line = rd.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var c = line[0];
            if (char.IsDigit(c) || c == '+' || c == '-' || c == '*')
            {
                // A cost line: "position cost cost...". The position may be "+3" or "*" (relative) in other tools; Xdebug writes plain numbers.
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= timeCol + 1 || !double.TryParse(parts[1 + timeCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var cost)) continue;
                var ms = cost * unitMs;
                if (afterCalls)
                {
                    // The line after calls= is what the call cost in total: part of the caller's inclusive time, and the callee's own inclusive time.
                    Get(curFn, curFile).Incl += ms;
                    var callee = Get(callFn, callFile);
                    callee.Calls += callCount;
                    afterCalls = false;
                }
                else
                {
                    var a = Get(curFn, curFile);
                    a.Self += ms; a.Incl += ms;
                    total += ms;
                }
                continue;
            }
            if (line.StartsWith("fl=", StringComparison.Ordinal) || line.StartsWith("fi=", StringComparison.Ordinal) || line.StartsWith("fe=", StringComparison.Ordinal))
                curFile = Resolve(files, line[3..]);
            else if (line.StartsWith("fn=", StringComparison.Ordinal))
                curFn = Resolve(fns, line[3..]);
            else if (line.StartsWith("cfl=", StringComparison.Ordinal) || line.StartsWith("cfi=", StringComparison.Ordinal))
                callFile = Resolve(files, line[4..]);
            else if (line.StartsWith("cfn=", StringComparison.Ordinal))
                callFn = Resolve(fns, line[4..]);
            else if (line.StartsWith("calls=", StringComparison.Ordinal))
            {
                var p = line[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                long.TryParse(p.Length > 0 ? p[0] : "1", out callCount);
                afterCalls = true;
            }
            else if (line.StartsWith("cmd:", StringComparison.Ordinal)) cmd = line[4..].Trim();
            else if (line.StartsWith("events:", StringComparison.Ordinal))
            {
                var ev = line[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                timeCol = Math.Max(0, Array.FindIndex(ev, e => e.StartsWith("Time", StringComparison.OrdinalIgnoreCase)));
                if (ev.Length > timeCol && ev[timeCol].Contains("(1ms)", StringComparison.OrdinalIgnoreCase)) unitMs = 1;
                else if (ev.Length > timeCol && ev[timeCol].Contains("(1us)", StringComparison.OrdinalIgnoreCase)) unitMs = 0.001;
            }
        }

        var list = agg.Values
            .OrderByDescending(a => a.Self)
            .Take(Math.Clamp(top, 1, 500))
            .Select(a => new XdebugFunction(a.Name, a.File, a.Calls, Math.Round(a.Self, 2), Math.Round(a.Incl, 2)))
            .ToArray();
        return new XdebugSummary(source, cmd, Math.Round(total, 2), agg.Count, list);
    }
}
