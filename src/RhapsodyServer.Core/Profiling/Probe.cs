using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhapsodyServer.Core.Profiling;

/// <summary>
/// The small script the collector adds to HTML pages (served same-origin as /__rs/probe.js, so it works on any site and
/// needs no change to the site's code or its Content-Security-Policy "self" rules), and the parser for what it sends back.
/// </summary>
public static class BrowserProbe
{
    public const string ScriptPath = "/__rs/probe.js";
    public const string BeaconPath = "/__rs/beacon";
    public const int MaxBeaconBytes = 4096;

    static readonly Regex IdRx = new("^[0-9a-f]{16}$", RegexOptions.Compiled);

    /// <summary>Measures with the browser's own PerformanceObserver APIs and sends one small JSON message (and a final one when the tab is left).</summary>
    public const string Script = """
        (function () {
          var s = document.currentScript;
          if (s && s.getAttribute('data-hints') === '1') {   // template hints are on for this site: load the page overlay
            var hs = document.createElement('script'); hs.src = '/__rs/hints.js'; hs.async = true;
            (document.head || document.documentElement).appendChild(hs);
          }
          if (!s || !window.PerformanceObserver) return;
          var rid = s.getAttribute('data-rid'), key = s.getAttribute('data-key');
          var m = { id: rid, key: key }, cls = 0, inp = 0, sent = '';
          function obs(type, cb) {
            try { new PerformanceObserver(function (l) { l.getEntries().forEach(cb); }).observe({ type: type, buffered: true }); } catch (e) {}
          }
          obs('paint', function (e) { if (e.name === 'first-contentful-paint') m.fcp = e.startTime; });
          obs('largest-contentful-paint', function (e) { m.lcp = e.startTime; });
          obs('layout-shift', function (e) { if (!e.hadRecentInput) { cls += e.value; m.cls = cls; } });
          obs('event', function (e) { if (e.interactionId && e.duration > inp) { inp = e.duration; m.inp = inp; } });
          function nav() {
            var n = performance.getEntriesByType('navigation')[0];
            if (!n) return;
            m.ttfb = n.responseStart;
            if (n.domContentLoadedEventEnd > 0) m.dcl = n.domContentLoadedEventEnd;
            if (n.loadEventEnd > 0) m.load = n.loadEventEnd;
            var r = performance.getEntriesByType('resource'), b = n.transferSize || 0;
            r.forEach(function (x) { b += x.transferSize || 0; });
            m.bytes = b; m.res = r.length;
          }
          function send() {
            nav();
            var body = JSON.stringify(m);
            if (body === sent) return;
            sent = body;
            try { navigator.sendBeacon('/__rs/beacon', body); } catch (e) {}
          }
          function later() { setTimeout(send, 2500); }
          if (document.readyState === 'complete') later(); else addEventListener('load', later);
          document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'hidden') send(); });
        })();
        """;

    /// <summary>
    /// Turns the probe's JSON into a record. Null for anything that isn't exactly what the probe sends: wrong key, bad id,
    /// or the wrong shape. Numbers are clamped, so nothing a web page sends can be larger than a real measurement.
    /// </summary>
    public static BrowserRecord? ParseBeacon(string json, string expectedKey)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxBeaconBytes) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { return null; }
        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(string name, double max)
        {
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
            var d = v.GetDouble();
            return double.IsFinite(d) && d >= 0 ? Math.Min(d, max) : null;
        }

        var key = Str("key");
        if (key == null || !FixedEquals(key, expectedKey)) return null;
        var id = Str("id");
        if (id == null || !IdRx.IsMatch(id)) return null;

        const double hour = 3_600_000;
        return new BrowserRecord
        {
            RequestId = id,
            ReceivedAt = DateTimeOffset.UtcNow,
            TtfbMs = Num("ttfb", hour),
            FcpMs = Num("fcp", hour),
            LcpMs = Num("lcp", hour),
            Cls = Num("cls", 100),
            InpMs = Num("inp", hour),
            DomContentLoadedMs = Num("dcl", hour),
            LoadMs = Num("load", hour),
            TransferBytes = Num("bytes", 1e12) is { } b ? (long)b : null,
            Resources = Num("res", 100000) is { } r ? (int)r : null,
        };
    }

    static bool FixedEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// The beacon and probe are reached through the sites' own web server, so a browser Origin of the site itself
    /// (https://myapp.test) is expected. Anything else (another website, no host) is refused.
    /// </summary>
    public static bool OriginAllowed(string? origin, string tld)
    {
        if (string.IsNullOrEmpty(origin)) return true;   // no Origin header: not a cross-site browser request
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != "http" && u.Scheme != "https") return false;
        return u.Host.EndsWith("." + tld, StringComparison.OrdinalIgnoreCase);
    }
}
