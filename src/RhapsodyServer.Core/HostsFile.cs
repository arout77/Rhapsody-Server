using System.Diagnostics;

namespace RhapsodyServer.Core;

/// <summary>
/// Keeps a single marked block in the Windows hosts file. Everything outside the block is never touched.
/// Needs write access once: GrantCurrentUserAccessAsync does one UAC prompt, after which no further elevation is needed.
/// </summary>
public static class HostsFile
{
    public const string Begin = "# >>> Rhapsody Server (managed block, do not edit)";
    public const string End = "# <<< Rhapsody Server";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static string Merge(string existing, IEnumerable<string> domains)
    {
        var nl = existing.Contains("\r\n") ? "\r\n" : existing.Contains('\n') ? "\n" : Environment.NewLine;
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();

        var b = lines.FindIndex(l => l.Trim() == Begin);
        if (b >= 0)
        {
            var e = lines.FindIndex(b, l => l.Trim() == End);
            lines.RemoveRange(b, (e >= 0 ? e : lines.Count - 1) - b + 1);
        }
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);

        var list = domains.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add(Begin);
            lines.AddRange(list.Select(d => $"127.0.0.1 {d}"));
            lines.Add(End);
        }
        return lines.Count == 0 ? "" : string.Join(nl, lines) + nl;
    }

    /// <summary>True if this process can already write the hosts file (so no admin prompt is needed).</summary>
    public static bool CanWrite(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return false;
        try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); return true; }
        catch { return false; }
    }

    /// <returns>true if the file was rewritten.</returns>
    public static bool Apply(IEnumerable<string> domains, string? path = null)
    {
        path ??= DefaultPath;
        var current = File.Exists(path) ? File.ReadAllText(path) : "";
        var updated = Merge(current, domains);
        if (updated == current) return false;   // skip pointless writes (and antivirus noise)
        try { File.WriteAllText(path, updated); }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                "Rhapsody Server can't edit the hosts file yet. Use 'Grant hosts access' once (one admin prompt).", ex);
        }
        return true;
    }

    public static async Task GrantCurrentUserAccessAsync(string? path = null)
    {
        path ??= DefaultPath;
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var psi = new ProcessStartInfo("icacls", $"\"{path}\" /grant \"{user}:M\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start icacls");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"icacls failed with code {p.ExitCode}");
    }
}
