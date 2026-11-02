using System.Collections.Concurrent;
using System.Diagnostics;

namespace RhapsodyServer.Core;

public enum ServiceState { Stopped, Starting, Running, Crashed }

public sealed record StopCommand(string Exe, IReadOnlyList<string> Args);

public sealed record ServiceSpec(
    string Id,
    string Name,
    string Exe,
    IReadOnlyList<string> Args,
    string WorkDir,
    string LogFile,
    IReadOnlyDictionary<string, string>? Env = null,
    int? ReadyPort = null,
    StopCommand? Stop = null);

/// <summary>
/// One supervised child process. Status is event-driven (Process.Exited), never polled:
/// idle cost is zero. Readiness polling happens only during startup, on a 50 ms tick.
/// </summary>
public sealed class ManagedService : IDisposable
{
    public ServiceSpec Spec { get; }
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public int? LastExitCode { get; private set; }
    public event Action<ManagedService>? StateChanged;

    Process? _proc;
    StreamWriter? _log;
    volatile bool _stopping;
    readonly object _gate = new();

    public ManagedService(ServiceSpec spec) => Spec = spec;

    void Set(ServiceState s)
    {
        lock (_gate) { if (State == s) return; State = s; }
        StateChanged?.Invoke(this);
    }

    void Log(string? line)
    {
        if (line == null) return;
        lock (_gate) { try { _log?.WriteLine(line); } catch (ObjectDisposedException) { } }
    }

    void CloseLog() { lock (_gate) { _log?.Dispose(); _log = null; } }

    public async Task StartAsync(TimeSpan? readyTimeout = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (State is ServiceState.Starting or ServiceState.Running) return;
            _stopping = false;
        }
        Set(ServiceState.Starting);

        Directory.CreateDirectory(Path.GetDirectoryName(Spec.LogFile)!);
        lock (_gate)
        {
            _log = new StreamWriter(new FileStream(Spec.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }

        var psi = new ProcessStartInfo(Spec.Exe)
        {
            WorkingDirectory = Spec.WorkDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in Spec.Args) psi.ArgumentList.Add(a);
        if (Spec.Env != null) foreach (var kv in Spec.Env) psi.Environment[kv.Key] = kv.Value;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Log(e.Data);
        p.ErrorDataReceived += (_, e) => Log(e.Data);
        p.Exited += (_, _) => OnExited(p);

        try { p.Start(); }
        catch (Exception ex)
        {
            Log($"[rhapsody-server] failed to start: {ex.Message}");
            CloseLog();
            Set(ServiceState.Crashed);
            throw new InvalidOperationException($"{Spec.Name} could not start: {ex.Message}", ex);
        }
        _proc = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (Spec.ReadyPort is int port)
        {
            var deadline = DateTime.UtcNow + (readyTimeout ?? TimeSpan.FromSeconds(20));
            while (DateTime.UtcNow < deadline && !p.HasExited && !PortProbe.IsListening(port))
                await Task.Delay(50, ct);
            if (!p.HasExited && !PortProbe.IsListening(port))
                Log($"[rhapsody-server] port {port} not listening after timeout; marking running anyway");
        }
        else
        {
            await Task.Delay(150, ct); // just long enough to catch an instant crash
        }

        if (p.HasExited)
            throw new InvalidOperationException(
                $"{Spec.Name} exited during startup (code {LastExitCode}). See {Spec.LogFile}");

        if (State == ServiceState.Starting) Set(ServiceState.Running);
    }

    void OnExited(Process p)
    {
        if (!ReferenceEquals(p, _proc) && _proc != null) return; // stale process from an earlier start
        try { LastExitCode = p.ExitCode; } catch { }
        Log($"[rhapsody-server] process exited (code {LastExitCode})");
        Set(_stopping ? ServiceState.Stopped : ServiceState.Crashed);
    }

    public async Task StopAsync(TimeSpan? grace = null)
    {
        var p = _proc;
        if (p == null || p.HasExited)
        {
            CloseLog();
            Set(ServiceState.Stopped);
            return;
        }

        _stopping = true;
        if (Spec.Stop is { } stop)
        {
            try { await ProcessRunner.RunAsync(stop.Exe, stop.Args, Spec.WorkDir, Spec.Env, TimeSpan.FromSeconds(10)); }
            catch { /* fall through to kill */ }
            try
            {
                using var cts = new CancellationTokenSource(grace ?? TimeSpan.FromSeconds(8));
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }
        if (!p.HasExited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            await p.WaitForExitAsync();
        }
        CloseLog();
        Set(ServiceState.Stopped);
    }

    public void Dispose()
    {
        try { if (_proc is { HasExited: false }) { _stopping = true; _proc.Kill(true); } } catch { }
        CloseLog();
    }
}

public sealed class ProcessSupervisor : IDisposable
{
    readonly ConcurrentDictionary<string, ManagedService> _services = new();
    public event Action<ManagedService>? StateChanged;

    /// <summary>Adds the service, or returns the existing one. A stopped/crashed service is rebuilt so spec changes apply.</summary>
    public ManagedService Register(ServiceSpec spec)
    {
        lock (_services)
        {
            if (_services.TryGetValue(spec.Id, out var existing))
            {
                if (existing.State is ServiceState.Running or ServiceState.Starting) return existing;
                existing.StateChanged -= Forward;
                existing.Dispose();
            }
            var svc = new ManagedService(spec);
            svc.StateChanged += Forward;
            _services[spec.Id] = svc;
            return svc;
        }
    }

    void Forward(ManagedService s) => StateChanged?.Invoke(s);

    public ManagedService? Get(string id) => _services.TryGetValue(id, out var s) ? s : null;
    public IReadOnlyCollection<ManagedService> All => _services.Values.ToArray();

    public Task StopAllAsync() => Task.WhenAll(All.Select(s => s.StopAsync()));

    public void Dispose() { foreach (var s in All) s.Dispose(); }
}
