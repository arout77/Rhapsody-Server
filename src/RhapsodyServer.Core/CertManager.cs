namespace RhapsodyServer.Core;

/// <summary>Thin wrapper around mkcert. One small cert per site: no wildcard-under-TLD browser surprises.</summary>
public sealed class CertManager
{
    readonly ServerPaths _paths;
    readonly string _mkcertExe;

    public CertManager(ServerPaths paths, string mkcertExe) { _paths = paths; _mkcertExe = mkcertExe; }

    public string CertFile(string domain) => Path.Combine(_paths.Certs, domain + ".pem");
    public string KeyFile(string domain) => Path.Combine(_paths.Certs, domain + "-key.pem");

    IReadOnlyDictionary<string, string> Env => new Dictionary<string, string>
    {
        ["CAROOT"] = Path.Combine(_paths.Certs, "ca")
    };

    public static string[] IssueArgs(string cert, string key, params string[] names) =>
        new[] { "-cert-file", cert, "-key-file", key }.Concat(names).ToArray();

    public async Task InstallCaAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.Combine(_paths.Certs, "ca"));
        var r = await ProcessRunner.RunAsync(_mkcertExe, new[] { "-install" }, env: Env, ct: ct);
        if (!r.Ok) throw new InvalidOperationException("mkcert -install failed:\n" + r.Output);
    }

    public async Task EnsureAsync(string domain, CancellationToken ct = default)
    {
        if (File.Exists(CertFile(domain)) && File.Exists(KeyFile(domain))) return;
        var r = await ProcessRunner.RunAsync(_mkcertExe, IssueArgs(CertFile(domain), KeyFile(domain), domain), env: Env, ct: ct);
        if (!r.Ok) throw new InvalidOperationException($"mkcert failed for {domain}:\n{r.Output}");
    }
}
