namespace RhapsodyServer.Core;

public static class Util
{
    /// <summary>nginx/Apache want forward slashes even on Windows.</summary>
    public static string Fwd(string path) => path.Replace('\\', '/');
}

public sealed class ServerPaths
{
    public string Root { get; }
    public string Bin => Path.Combine(Root, "bin");
    public string Etc => Path.Combine(Root, "etc");
    public string Logs => Path.Combine(Root, "logs");
    public string Data => Path.Combine(Root, "data");
    public string Certs => Path.Combine(Root, "certs");
    public string Downloads => Path.Combine(Root, "downloads");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public ServerPaths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable("RHAPSODY_SERVER_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RhapsodyServer");
    }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Bin, Etc, Logs, Data, Certs, Downloads })
            Directory.CreateDirectory(d);
    }

    public string ComponentDir(string id, string version) => Path.Combine(Bin, id, version);
}
