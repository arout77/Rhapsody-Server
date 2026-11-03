using System.Text;
using System.Text.RegularExpressions;

namespace RhapsodyServer.Core.Ai;

/// <summary>
/// Shrinks a log to something a free-tier model can take in. The key trick is collapsing repeats: a PHP log with
/// 400 identical deprecation warnings becomes one line marked "(x400)", so the one error that matters stays in view.
/// </summary>
public static class LogCondenser
{
    static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    public static string Condense(string? text, int maxChars, int maxEntries = 160)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // Group repeats. A group sits where its LAST line was and shows that latest line: an error that keeps
        // happening is recent news, and the newest wording and timestamp are the useful ones.
        var latest = new Dictionary<string, (int At, string Line)>();
        var count = new Dictionary<string, int>();
        var at = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.Length > 400) line = line[..400] + "…";
            var key = Digits.Replace(line, "#");
            count[key] = count.GetValueOrDefault(key) + 1;
            latest[key] = (at++, line);
        }

        var entries = latest.OrderBy(kv => kv.Value.At)
            .Select(kv => count[kv.Key] > 1 ? $"{kv.Value.Line}  (x{count[kv.Key]})" : kv.Value.Line).ToList();
        var dropped = false;
        if (entries.Count > maxEntries) { entries = entries.Skip(entries.Count - maxEntries).ToList(); dropped = true; }

        // Still too big: drop the oldest entries first, the newest are the most relevant.
        var total = entries.Sum(e => e.Length + 1);
        var start = 0;
        while (total > maxChars && start < entries.Count - 1) { total -= entries[start].Length + 1; start++; dropped = true; }

        var sb = new StringBuilder();
        if (dropped) sb.AppendLine("(older log lines left out)");
        for (var i = start; i < entries.Count; i++) sb.AppendLine(entries[i]);
        var result = sb.ToString().TrimEnd();
        return result.Length > maxChars ? "…" + result[^(maxChars - 1)..] : result;   // one giant last line
    }
}
