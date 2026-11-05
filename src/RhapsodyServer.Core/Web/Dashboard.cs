namespace RhapsodyServer.Core.Web;

public sealed record ServiceDto(string Id, string Name, string State, int? Port, bool Toggleable, string Detail);
public sealed record SiteDto(string Name, string Domain, string Url, string Root, string PublicDir, string Php, bool Https);
public sealed record SettingsDto(string Tld, string WebServer, string DefaultPhp, string[] PhpBranches,
    int HttpPort, int HttpsPort, int MariaDbPort, int RedisPort, int SmtpPort, int MailUiPort, string SitesRoot,
    string DataRoot = "", string LogsDir = "");
public sealed record DashboardState(ServiceDto[] Services, SiteDto[] Sites, SettingsDto Settings, bool HostsWritable, bool Windows, bool CanPickFolder = false,
    bool CanOpenFolder = false);
/// <param name="UpdateTo">A newer build of this installed series that the vendor offers (apache and nginx only), else null.</param>
public sealed record VersionDto(string Series, string Version, bool Installed, bool Bundled, bool Available, bool Active, bool Removable, string? UpdateTo = null);
public sealed record ComponentDto(string Id, string Name, string Note, bool Offline, VersionDto[] Versions);

public sealed record SiteRequest(string Name, string Root, string? PublicDir, string Php, bool? Https);
public sealed record ScaffoldRequestDto(string Name, string Php, string? DbName = null, string? DbUser = null, string? DbPass = null);
public sealed record PhpRequest(string Php);
public sealed record WebServerRequest(string Kind);

public sealed record ModuleDto(string Name, string Description, bool Enabled, bool Locked, string? Note);
/// <summary>PHP extensions of one installed PHP. <c>Branches</c> are the installed ones; <c>Branch</c> is the one listed.</summary>
public sealed record PhpModulesDto(string[] Branches, string? Branch, ModuleDto[] Extensions, bool WorkersRunning, string? IniPath = null);
/// <summary><c>Installed</c> false = nothing to list yet. <c>Serving</c> = Apache is the selected web server.</summary>
public sealed record ApacheModulesDto(bool Installed, string? Version, bool Serving, bool Running, ModuleDto[] Modules);
public sealed record ModulesRequest(string[] Enabled);
public sealed record PickFolderRequest(string? Initial);
public sealed record VersionRequest(string Id, string Series);

/// <param name="Target"><c>sites</c> (where new sites are created), <c>site</c> (one site's folder, by Name), <c>data</c> (the app's own files) or <c>logs</c>.</param>
public sealed record OpenFolderRequest(string Target, string? Name);

/// <param name="Id">Names one log file. The page only ever sends this; the app owns the path.</param>
/// <param name="Group">The program the file belongs to (Nginx, PHP, MariaDB…); the page uses it for the list headings and the icon.</param>
/// <param name="ExplainId">The service whose log the AI "Explain" reads for this file, or null.</param>
public sealed record LogSourceDto(string Id, string Name, string Group, string Detail, long Size, string? ExplainId);
/// <param name="Truncated">True when the file is bigger than what was read, so these are only its last lines.</param>
public sealed record LogContentDto(string Text, long Size, bool Truncated);
/// <param name="Branch">Which PHP's php.ini; null = the default PHP. The page never names a path.</param>
/// <param name="Action"><c>edit</c> (open in the person's editor) or <c>reveal</c> (show it in its folder).</param>
public sealed record PhpIniRequest(string? Branch, string Action);

public sealed record AiProviderDto(string Id, string Name, string BaseUrl, string Model, bool NeedsKey, string? KeyUrl, string? Note);
/// <summary>The AI page's state. Keys never leave the app: the page only learns whether one is saved.</summary>
public sealed record AiConfigDto(bool Enabled, bool Ready, Ai.AiEndpointView Primary, Ai.AiEndpointView? Fallback, AiProviderDto[] Providers);
/// <param name="Kind"><c>error</c> (Text is the error) or <c>log</c> (LogId names a service log the app reads itself).</param>
public sealed record ExplainRequest(string Kind, string? Title, string? Text, string? LogId);
/// <param name="Sent">Exactly what was sent to the provider (after redaction), so the person can see it.</param>
public sealed record AiAnswerDto(string Answer, string Provider, string Model, bool UsedFallback, string? FallbackNote, string Sent);
public sealed record AiTestRequest(string? Which);

/// <param name="Question">What the developer typed, in their own words.</param>
/// <param name="Site">Optional: the site the question is about (narrows where the assistant looks first).</param>
/// <param name="AllowRequests">True lets the assistant load pages of the person's sites (GET only, through the local web server).</param>
/// <param name="AskId">Made by the page; progress events carry it so the page can tell its own question's progress from another's.</param>
public sealed record AiAskRequest(string Question, string? Site, bool AllowRequests, string? AskId);
/// <param name="Result">Exactly what the provider was shown for this lookup (after scrubbing).</param>
public sealed record AiAskStepDto(int Number, string Tool, string Summary, string Result, bool Failed);
public sealed record AiAskDto(string Answer, string Provider, string Model, bool UsedFallback, string? FallbackNote, AiAskStepDto[] Steps, bool HitLimit);

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

    /// <summary>Shows a known folder in Windows Explorer. Throws ArgumentException for an unknown target or a folder that is gone.</summary>
    void OpenFolder(OpenFolderRequest req);

    /// <summary>Every log file that exists right now, grouped by program.</summary>
    LogSourceDto[] GetLogSources();
    /// <summary>The last lines of one log file from <see cref="GetLogSources"/>. Throws ArgumentException for an id that isn't one.</summary>
    LogContentDto ReadLogSource(string id, int lines);

    /// <summary>Opens (or shows in its folder) the php.ini a PHP version really loads. Throws ArgumentException for an unknown action or a PHP that isn't installed.</summary>
    void OpenPhpIni(PhpIniRequest req);
    /// <summary>Restarts that PHP's workers so a changed php.ini is read. Does nothing if they aren't running.</summary>
    Task RestartPhpAsync(string branch, IProgress<string> p, CancellationToken ct);

    /// <summary>Asks the running web server for the site's home page and gathers what the server and PHP logged, as plain text for the person (or a bug report).</summary>
    Task<string> CheckSiteAsync(string name, CancellationToken ct);

    // ---- optional AI helper ("Explain this"). Not operations: they don't take the busy lock.
    AiConfigDto GetAi();
    void SaveAi(Ai.AiSaveInput input);
    Task<Ai.AiTestResult> TestAiAsync(string which, CancellationToken ct);
    Task<AiAnswerDto> ExplainAsync(ExplainRequest req, CancellationToken ct);

    // ---- "Ask": free-form questions the assistant answers by looking at logs, the profiler and the site's files.
    // Default bodies so a host that doesn't offer it (a test fake) needs no change.
    /// <param name="progress">Short present-tense lines ("Reading the nginx log…") while it works.</param>
    Task<AiAskDto> AskAiAsync(AiAskRequest req, IProgress<string>? progress, CancellationToken ct) =>
        throw new NotSupportedException("Ask isn't available in this host.");
    void CancelAi() { }
}
