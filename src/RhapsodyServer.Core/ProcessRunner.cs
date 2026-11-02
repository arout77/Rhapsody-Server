using System.Diagnostics;
using System.Text;

namespace RhapsodyServer.Core;

public sealed record RunResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs a short-lived process to completion. Uses ArgumentList so paths with spaces just work.</summary>
public static class ProcessRunner
{
    public static async Task<RunResult> RunAsync(
        string exe, IEnumerable<string> args, string? workDir = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workDir != null) psi.WorkingDirectory = workDir;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var gate = new object();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            throw;
        }
        p.WaitForExit(); // flush async output handlers
        return new RunResult(p.ExitCode, sb.ToString());
    }
}
