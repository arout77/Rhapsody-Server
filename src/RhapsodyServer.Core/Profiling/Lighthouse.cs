using System.Text.Json;

namespace RhapsodyServer.Core.Profiling;

public sealed record LighthouseScore(string Id, string Title, int Score);
public sealed record LighthouseMetric(string Id, string Title, string Value, string Rating);
/// <param name="ReportFile">The full HTML report Lighthouse wrote.</param>
public sealed record LighthouseResult(string Url, LighthouseScore[] Scores, LighthouseMetric[] Metrics, string ReportFile, string Version);
/// <param name="Ready">False when something Lighthouse needs is missing; <paramref name="Missing"/> says what, in plain words.</param>
public sealed record LighthouseStatus(bool Ready, string? Missing, string? Browser);

/// <summary>
/// Optional "Lighthouse" button. Lighthouse is a Node program that drives Chrome, so it is only offered when Node and a
/// Chromium browser are already on this computer; nothing is installed or bundled. It runs through npx (npm's runner),
/// which downloads Lighthouse into npm's own cache the first time.
/// </summary>
public static class LighthouseRunner
{
    public static readonly string[] CategoryIds = { "performance", "accessibility", "best-practices", "seo" };

    static readonly (string Id, string Title)[] MetricIds =
    {
        ("first-contentful-paint", "First Contentful Paint"),
        ("largest-contentful-paint", "Largest Contentful Paint"),
        ("total-blocking-time", "Total Blocking Time"),
        ("cumulative-layout-shift", "Cumulative Layout Shift"),
        ("speed-index", "Speed Index"),
        ("server-response-time", "Server response time"),
    };

    /// <summary>First match of a program name on PATH (tries the Windows .cmd/.exe forms too). Null if none.</summary>
    public static string? FindOnPath(string name, string? pathVar = null)
    {
        var dirs = (pathVar ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var names = OperatingSystem.IsWindows() ? new[] { name + ".cmd", name + ".exe", name + ".bat" } : new[] { name };
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var f = Path.Combine(d.Trim('"'), n); if (File.Exists(f)) return f; } catch (ArgumentException) { }
            }
        return null;
    }

    /// <summary>Chrome, Edge, Chromium or Brave in their usual Windows places (or on PATH elsewhere). Lighthouse takes any of them via CHROME_PATH.</summary>
    public static string? FindBrowser(Func<string, bool>? exists = null, Func<string, string?>? env = null)
    {
        exists ??= File.Exists;
        env ??= Environment.GetEnvironmentVariable;
        var found = env("CHROME_PATH");
        if (!string.IsNullOrEmpty(found) && exists(found)) return found;

        var roots = new[] { env("ProgramFiles"), env("ProgramFiles(x86)"), env("LocalAppData") }.Where(x => !string.IsNullOrEmpty(x));
        var rels = new[]
        {
            @"Google\Chrome\Application\chrome.exe",
            @"Microsoft\Edge\Application\msedge.exe",
            @"BraveSoftware\Brave-Browser\Application\brave.exe",
            @"Chromium\Application\chrome.exe",
        };
        foreach (var rel in rels)
            foreach (var root in roots)
            {
                var p = Path.Combine(root!, rel);
                if (exists(p)) return p;
            }
        foreach (var n in new[] { "google-chrome", "chromium", "chromium-browser", "microsoft-edge" })
            if (FindOnPath(n) is { } onPath) return onPath;
        return null;
    }

    public static LighthouseStatus Status()
    {
        var npx = FindOnPath("npx");
        if (npx == null) return new LighthouseStatus(false, "Node.js isn't installed (Lighthouse runs on it). Install Node from nodejs.org, then reopen Rhapsody Server.", null);
        var browser = FindBrowser();
        if (browser == null) return new LighthouseStatus(false, "No Chrome, Edge or Brave was found. Lighthouse needs one of them.", null);
        return new LighthouseStatus(true, null, Path.GetFileNameWithoutExtension(browser));
    }

    /// <summary>The arguments for npx. The URL is always one the app built from a site name, never text from the page.</summary>
    public static string[] Arguments(string url, string outputBase) => new[]
    {
        "--yes", "lighthouse", url,
        "--output=json", "--output=html", "--output-path=" + outputBase,
        "--only-categories=" + string.Join(",", CategoryIds),
        "--chrome-flags=--headless=new", "--quiet", "--no-enable-error-reporting",
    };

    public static async Task<LighthouseResult> RunAsync(string url, string outputDir, CancellationToken ct)
    {
        var st = Status();
        if (!st.Ready) throw new InvalidOperationException(st.Missing);
        Directory.CreateDirectory(outputDir);
        PruneOld(outputDir, keep: 10);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = Path.Combine(outputDir, "lighthouse-" + stamp);
        var env = new Dictionary<string, string> { ["CHROME_PATH"] = FindBrowser()! };
        var r = await ProcessRunner.RunAsync(FindOnPath("npx")!, Arguments(url, baseName), outputDir, env, TimeSpan.FromMinutes(5), ct);
        var json = new[] { baseName + ".report.json", baseName + ".json" }.FirstOrDefault(File.Exists);
        if (!r.Ok || json == null)
        {
            var tail = string.Join("\n", r.Output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(8));
            throw new InvalidOperationException("Lighthouse didn't finish" + (tail.Length > 0 ? ":\n" + tail : "."));
        }
        var html = new[] { baseName + ".report.html", baseName + ".html" }.FirstOrDefault(File.Exists) ?? json;
        return ParseReport(File.ReadAllText(json), url, html);
    }

    /// <summary>Scores (0-100) and the main timings out of Lighthouse's JSON report.</summary>
    public static LighthouseResult ParseReport(string json, string url, string reportFile)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var scores = new List<LighthouseScore>();
        if (root.TryGetProperty("categories", out var cats))
            foreach (var id in CategoryIds)
                if (cats.TryGetProperty(id, out var c) && c.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number)
                    scores.Add(new LighthouseScore(id, c.TryGetProperty("title", out var t) ? t.GetString() ?? id : id, (int)Math.Round(s.GetDouble() * 100)));

        var metrics = new List<LighthouseMetric>();
        if (root.TryGetProperty("audits", out var audits))
            foreach (var (id, title) in MetricIds)
                if (audits.TryGetProperty(id, out var a))
                {
                    var shown = a.TryGetProperty("displayValue", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString()! : "";
                    var score = a.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : (double?)null;
                    metrics.Add(new LighthouseMetric(id, title, shown, score == null ? "" : score >= 0.9 ? "good" : score >= 0.5 ? "ok" : "poor"));
                }
        var version = root.TryGetProperty("lighthouseVersion", out var v) ? v.GetString() ?? "" : "";
        return new LighthouseResult(url, scores.ToArray(), metrics.ToArray(), reportFile, version);
    }

    static void PruneOld(string dir, int keep)
    {
        try
        {
            var old = new DirectoryInfo(dir).GetFiles("lighthouse-*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep * 2).ToList();
            foreach (var f in old) f.Delete();
        }
        catch (IOException) { }
    }
}
