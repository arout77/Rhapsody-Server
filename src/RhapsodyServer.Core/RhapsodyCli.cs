using System.Text.RegularExpressions;

namespace RhapsodyServer.Core;

/// <summary>One framework command the dashboard can run. <see cref="Args"/> is the whole argument list after "php rhapsody".</summary>
/// <param name="NeedsName">A name is appended (make:controller UserController). The page asks for it.</param>
/// <param name="NeedsDb">The command talks to the database, so MariaDB has to be running.</param>
public sealed record RhapsodyCommand(string Id, string Label, string Description, string[] Args,
    bool NeedsName = false, bool NeedsDb = false, string NameHint = "");

public sealed record RhapsodyCommandGroup(string Title, RhapsodyCommand[] Commands);

/// <summary>A "set this up for me" choice offered when a new Rhapsody site is created.</summary>
public sealed record ScaffoldStep(string Id, string Label, string Description, string[] Args, bool Default = false, bool NeedsDb = false);

/// <summary>
/// The Rhapsody framework's command line (<c>php rhapsody &lt;command&gt;</c>), as a fixed list.
/// The dashboard can only ever pick from this list, never type a command, so it can't be used to run anything else.
/// </summary>
public static class RhapsodyCli
{
    /// <summary>Commands run on a new site right after it is created, in this order. Each one is optional.</summary>
    public static readonly IReadOnlyList<ScaffoldStep> ScaffoldSteps = new[]
    {
        new ScaffoldStep("auth", "Authentication tables", "Adds the users and password-reset tables (auth:install).",
            new[] { "auth:install" }, NeedsDb: true),
        new ScaffoldStep("queue", "Job queue", "Adds the queue tables and runs the migration (queue:install --migrate).",
            new[] { "queue:install", "--migrate" }, NeedsDb: true),
        new ScaffoldStep("react", "React and Vite", "Creates the React islands setup (react:install). You still run npm install yourself.",
            new[] { "react:install" }),
        new ScaffoldStep("proxies", "Pre-build service proxies", "Generates the lazy proxy classes up front so the first page load isn't the one to do it (build:proxies).",
            new[] { "build:proxies" }, Default: true),
        new ScaffoldStep("cache-warm", "Warm the template cache", "Compiles every Twig template ahead of time (app:cache-warm).",
            new[] { "app:cache-warm" }),
    };

    public static readonly IReadOnlyList<RhapsodyCommandGroup> Groups = new[]
    {
        new RhapsodyCommandGroup("Database", new[]
        {
            new RhapsodyCommand("migrate", "Run migrations", "Runs pending database migrations.", new[] { "migrate" }, NeedsDb: true),
            new RhapsodyCommand("auth:install", "Install auth tables", "Adds the users and password-reset tables.", new[] { "auth:install" }, NeedsDb: true),
            new RhapsodyCommand("queue:install", "Install queue tables", "Publishes the queue migration and runs it.", new[] { "queue:install", "--migrate" }, NeedsDb: true),
        }),
        new RhapsodyCommandGroup("Speed and caches", new[]
        {
            new RhapsodyCommand("cache:clear", "Clear cache", "Flushes the application cache.", new[] { "cache:clear" }),
            new RhapsodyCommand("app:cache-warm", "Warm template cache", "Compiles every Twig template ahead of time.", new[] { "app:cache-warm" }),
            new RhapsodyCommand("build:proxies", "Build proxies", "Pre-generates the lazy proxy classes for the container's services.", new[] { "build:proxies" }),
            new RhapsodyCommand("route:cache", "Cache routes", "Compiles the routes into one file. Run Clear routes after changing a route.", new[] { "route:cache" }),
            new RhapsodyCommand("route:clear", "Clear routes", "Removes the route cache file.", new[] { "route:clear" }),
        }),
        new RhapsodyCommandGroup("Project", new[]
        {
            new RhapsodyCommand("env:sync", "Sync .env", "Adds any variable that .env.example has and .env is missing.", new[] { "env:sync" }),
            new RhapsodyCommand("app:check-version", "Check for updates", "Asks Packagist whether a newer rhapsody-core exists.", new[] { "app:check-version" }),
            new RhapsodyCommand("module:list", "List modules", "Every module, whether it is installed, and whether it booted.", new[] { "module:list" }),
            new RhapsodyCommand("module:health", "Module health", "Modules that failed to boot, and their dependency checks.", new[] { "module:health" }),
            new RhapsodyCommand("react:install", "Install React and Vite", "Creates the React islands setup. You still run npm install yourself.", new[] { "react:install" }),
        }),
        new RhapsodyCommandGroup("Queue", new[]
        {
            new RhapsodyCommand("queue:work", "Run queued jobs", "Processes the queue until it is empty, then stops.", new[] { "queue:work", "--stop-when-empty" }, NeedsDb: true),
            new RhapsodyCommand("queue:failed", "List failed jobs", "Jobs that failed permanently.", new[] { "queue:failed" }, NeedsDb: true),
            new RhapsodyCommand("queue:retry", "Retry all failed", "Puts every failed job back on the queue.", new[] { "queue:retry", "all" }, NeedsDb: true),
        }),
        new RhapsodyCommandGroup("Generate", new[]
        {
            new RhapsodyCommand("make:controller", "Controller", "Creates a controller class.", new[] { "make:controller" }, NeedsName: true, NameHint: "UserController"),
            new RhapsodyCommand("make:model", "Model", "Creates a model class.", new[] { "make:model" }, NeedsName: true, NameHint: "Post"),
            new RhapsodyCommand("make:migration", "Migration", "Creates a database migration file.", new[] { "make:migration" }, NeedsName: true, NameHint: "CreatePostsTable"),
            new RhapsodyCommand("make:middleware", "Middleware", "Creates a middleware class.", new[] { "make:middleware" }, NeedsName: true, NameHint: "CheckAdminRole"),
            new RhapsodyCommand("make:event", "Event", "Creates an event class.", new[] { "make:event" }, NeedsName: true, NameHint: "OrderShipped"),
            new RhapsodyCommand("make:listener", "Listener", "Creates an event listener class.", new[] { "make:listener" }, NeedsName: true, NameHint: "SendOrderNotification"),
        }),
    };

    public static RhapsodyCommand? Find(string id) => Groups.SelectMany(g => g.Commands).FirstOrDefault(c => c.Id == id);

    /// <summary>A class or file name: starts with a letter, then letters, digits, underscores and folder slashes. No "..", no options.</summary>
    static readonly Regex NameRx = new(@"^[A-Za-z][A-Za-z0-9_/\\]{0,79}$");

    /// <summary>The name for a make: command, checked. Throws ArgumentException with a message the person can read.</summary>
    public static string CheckName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("Type a name first, such as UserController.");
        if (!NameRx.IsMatch(n) || n.Contains("..") || n.Contains("//") || n.Contains("\\\\") || n.EndsWith('/') || n.EndsWith('\\'))
            throw new ArgumentException("A name starts with a letter and uses only letters, digits and underscores (and / for a folder).");
        return n;
    }

    /// <summary>Is this folder a Rhapsody project: the <c>rhapsody</c> launcher and the core package are both there.</summary>
    public static bool IsRhapsodyProject(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try { return File.Exists(Path.Combine(root, "rhapsody")) && Directory.Exists(Path.Combine(root, "vendor", "arout", "rhapsody-core")); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    public const int MaxOutput = 100_000;

    /// <summary>Output without colour codes and carriage returns, and no longer than <see cref="MaxOutput"/> (the end is kept).</summary>
    public static string CleanOutput(string raw)
    {
        var t = Ansi.Replace(raw ?? "", "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ');
        if (t.Length > MaxOutput) t = "(earlier output left out)\n" + t[^MaxOutput..];
        return t;
    }

    /// <summary>
    /// Did it work? The exit code isn't enough: the runner returns 0 for a Symfony command that failed, and says so in the output instead.
    /// </summary>
    public static bool Succeeded(int exitCode, string cleanOutput) =>
        exitCode == 0 && !Regex.IsMatch(cleanOutput, @"(?m)^\s*(\[Runtime Error\]|Runtime Error\b|\[Error\]|Error:\s|\[[A-Za-z\\]+(Exception|Error)\])|PHP (Fatal|Parse) error")
                      && !cleanOutput.Contains("Available Commands:");   // an unknown command prints the command list and exits 0

    public sealed record Result(bool Ok, int ExitCode, string Output);

    /// <summary>Runs <c>php rhapsody args</c> in the project folder, with that site's PHP.</summary>
    public static async Task<Result> RunAsync(Stack stack, string root, string php, IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? extraEnv = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var env = stack.PhpEnv(php).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (extraEnv != null) foreach (var (k, v) in extraEnv) env[k] = v;
        var all = new List<string> { "rhapsody" };
        all.AddRange(args);
        var r = await ProcessRunner.RunAsync(stack.PhpExe(php), all, root, env, timeout ?? TimeSpan.FromMinutes(5), ct);
        var text = CleanOutput(r.Output);
        return new Result(Succeeded(r.ExitCode, text), r.ExitCode, text);
    }
}
