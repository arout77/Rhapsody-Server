namespace RhapsodyServer.Core.Web;

public sealed record ServiceDto(string Id, string Name, string State, int? Port, bool Toggleable, string Detail);
public sealed record SiteDto(string Name, string Domain, string Url, string Root, string PublicDir, string Php, bool Https);
public sealed record SettingsDto(string Tld, string WebServer, string DefaultPhp, string[] PhpBranches,
    int HttpPort, int HttpsPort, int MariaDbPort, int RedisPort, int SmtpPort, int MailUiPort, string SitesRoot);
public sealed record DashboardState(ServiceDto[] Services, SiteDto[] Sites, SettingsDto Settings, bool HostsWritable, bool Windows, bool CanPickFolder = false);
/// <param name="UpdateTo">A newer build of this installed series that the vendor offers (apache and nginx only), else null.</param>
public sealed record VersionDto(string Series, string Version, bool Installed, bool Bundled, bool Available, bool Active, bool Removable, string? UpdateTo = null);
public sealed record ComponentDto(string Id, string Name, string Note, bool Offline, VersionDto[] Versions);

public sealed record SiteRequest(string Name, string Root, string? PublicDir, string Php, bool? Https);
public sealed record ScaffoldRequestDto(string Name, string Php, string? DbName = null, string? DbUser = null, string? DbPass = null);
public sealed record PhpRequest(string Php);
public sealed record WebServerRequest(string Kind);

public sealed record ModuleDto(string Name, string Description, bool Enabled, bool Locked, string? Note);
/// <summary>PHP extensions of one installed PHP. <c>Branches</c> are the installed ones; <c>Branch</c> is the one listed.</summary>
public sealed record PhpModulesDto(string[] Branches, string? Branch, ModuleDto[] Extensions, bool WorkersRunning);
/// <summary><c>Installed</c> false = nothing to list yet. <c>Serving</c> = Apache is the selected web server.</summary>
public sealed record ApacheModulesDto(bool Installed, string? Version, bool Serving, bool Running, ModuleDto[] Modules);
public sealed record ModulesRequest(string[] Enabled);
public sealed record PickFolderRequest(string? Initial);
public sealed record VersionRequest(string Id, string Series);

public sealed record AiProviderDto(string Id, string Name, string BaseUrl, string Model, bool NeedsKey, string? KeyUrl, string? Note);
/// <summary>The AI page's state. Keys never leave the app: the page only learns whether one is saved.</summary>
public sealed record AiConfigDto(bool Enabled, bool Ready, Ai.AiEndpointView Primary, Ai.AiEndpointView? Fallback, AiProviderDto[] Providers);
/// <param name="Kind"><c>error</c> (Text is the error) or <c>log</c> (LogId names a service log the app reads itself).</param>
public sealed record ExplainRequest(string Kind, string? Title, string? Text, string? LogId);
/// <param name="Sent">Exactly what was sent to the provider (after redaction), so the person can see it.</param>
public sealed record AiAnswerDto(string Answer, string Provider, string Model, bool UsedFallback, string? FallbackNote, string Sent);
public sealed record AiTestRequest(string? Which);

/// <summary>Shows the operating system's folder chooser. Implemented by the tray app; the core library has no UI.</summary>
public interface IFolderPicker
{
    Task<string?> PickAsync(string? initialDir, CancellationToken ct);
}

/// <summary>
/// Everything the dashboard can do. The real implementation wraps Stack; tests and the UI demo use a fake,
/// which is what lets the web interface be exercised in a browser without any Windows binaries.
/// </summary>
public interface IDashboardBackend
{
    /// <summary>Raised (from any thread) when service state, sites or settings change.</summary>
    event Action? Changed;

    /// <summary>Raised (from any thread) when profiling recorded something new.</summary>
    event Action? ProfileChanged;

    DashboardState GetState();

    /// <summary>True when the host can show a native "choose a folder" window (the tray app can).</summary>
    bool CanPickFolder { get; }
    /// <summary>Opens that window and returns the chosen folder, or null if the user cancelled.</summary>
    Task<string?> PickFolderAsync(string? initial, CancellationToken ct);

    PhpModulesDto GetPhpModules(string? branch);
    ApacheModulesDto GetApacheModules();
    Task SetPhpExtensionsAsync(string branch, string[] enabled, IProgress<string> p, CancellationToken ct);
    Task SetApacheModulesAsync(string[] enabled, IProgress<string> p, CancellationToken ct);
    Profiling.ProfileSnapshot GetProfile();
    void ClearProfile();
    Task SetProfilingAsync(bool on, IProgress<string> p, CancellationToken ct);
    string ReadLog(string id, int lines);
    Task<ComponentDto[]> GetVersionsAsync(CancellationToken ct);

    Task StartAllAsync(IProgress<string> p, CancellationToken ct);
    Task StopAllAsync(IProgress<string> p, CancellationToken ct);
    Task StartServiceAsync(string id, IProgress<string> p, CancellationToken ct);
    Task StopServiceAsync(string id, IProgress<string> p, CancellationToken ct);

    Task AddSiteAsync(SiteRequest req, IProgress<string> p, CancellationToken ct);
    Task SetSitePhpAsync(string name, string php, IProgress<string> p, CancellationToken ct);
    Task RemoveSiteAsync(string name, IProgress<string> p, CancellationToken ct);
    Task ScaffoldAsync(ScaffoldRequestDto req, IProgress<string> p, CancellationToken ct);
    Task SwitchWebServerAsync(string kind, IProgress<string> p, CancellationToken ct);

    Task InstallVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct);
    Task SetActiveVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct);
    Task RemoveVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct);
    /// <summary>Installs the newest build of an installed series (apache, nginx).</summary>
    Task UpdateVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct);
    /// <summary>Installs an Apache Lounge zip the person downloaded (the way to pick an older or specific build).</summary>
    Task ImportApacheAsync(string fileName, Stream data, IProgress<string> p, CancellationToken ct);

    Task GrantHostsAccessAsync(IProgress<string> p, CancellationToken ct);

    // ---- optional AI helper ("Explain this"). Not operations: they don't take the busy lock.
    AiConfigDto GetAi();
    void SaveAi(Ai.AiSaveInput input);
    Task<Ai.AiTestResult> TestAiAsync(string which, CancellationToken ct);
    Task<AiAnswerDto> ExplainAsync(ExplainRequest req, CancellationToken ct);
}
