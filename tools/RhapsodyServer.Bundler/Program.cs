using RhapsodyServer.Core;

// Builds the "ships with the app" bundle: the latest PHP, Apache, nginx and MariaDB LTS plus the small tools.
//
//   dotnet run --project tools/RhapsodyServer.Bundler -- --dry-run           check every vendor page, download nothing
//   dotnet run --project tools/RhapsodyServer.Bundler -- --out bundle        download everything into .\bundle
//   dotnet run --project tools/RhapsodyServer.Bundler -- --php 8.5,8.4,8.3   bundle several PHP versions instead of just the latest
//   dotnet run --project tools/RhapsodyServer.Bundler -- --apache-zip C:\\Downloads\\httpd-2.4.66-250101-win64-VS17.zip
//                                                                             use an Apache you downloaded yourself (apachelounge.com often blocks scripts)
//   dotnet run --project tools/RhapsodyServer.Bundler -- --verify bundle     check a finished bundle is complete (publish.cmd does this)
//
// Run it on a machine with internet access (your PC, or a CI job), then ship the bundle folder next to RhapsodyServer.exe.

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var dry = args.Contains("--dry-run");
var verify = args.Contains("--verify");
var outDir = Path.GetFullPath(Arg("--out") ?? "bundle");
var php = Arg("--php")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (verify)
{
    var dir = Path.GetFullPath(Arg("--verify") is { } v && !v.StartsWith("--") ? v : "bundle");
    var problems = BundleCheck.Problems(dir);
    if (problems.Count == 0) { Console.WriteLine($"Bundle OK: {dir}"); return 0; }
    Console.Error.WriteLine($"The bundle in {dir} is not ready to ship:");
    foreach (var pr in problems) Console.Error.WriteLine("  - " + pr);
    Console.Error.WriteLine("\nRun bundle.cmd to fill it in (for Apache you can pass --apache-zip <file> if the download is blocked).");
    return 1;
}

var paths = new ServerPaths(Path.Combine(Path.GetTempPath(), "rhapsody-server-bundler"));
var installer = new Installer(paths);
var say = new Progress<string>(Console.WriteLine);

if (dry)
{
    Console.WriteLine("Checking each vendor (nothing is downloaded):\n");
    var bad = 0;
    foreach (var id in Catalog.VersionedIds)
    {
        try
        {
            var list = await PackageSources.ListAsync(installer.Http, id);
            var latest = PackageSources.Latest(id, list);
            if (latest == null) throw new InvalidOperationException("the page loaded but no downloads were recognised (the page layout may have changed; see Resolvers.cs)");
            Console.WriteLine($"  OK    {Catalog.Shapes[id].Name,-8} {list.Count} version(s); latest {latest.Version}\n          {latest.Url}");
        }
        catch (Exception ex) { bad++; Console.WriteLine($"  FAIL  {Catalog.Shapes[id].Name,-8} {ex.Message}"); }
    }
    foreach (var p in Catalog.Fixed)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, p.Url);
            using var res = await installer.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var ok = res.IsSuccessStatusCode || (int)res.StatusCode is 301 or 302 or 405;   // some hosts refuse HEAD
            if (!ok) bad++;
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {p.Name,-8} {p.Version}{(ok ? "" : $"  ({(int)res.StatusCode} from {p.Url})")}");
        }
        catch (Exception ex) { bad++; Console.WriteLine($"  FAIL  {p.Name,-8} {ex.Message}"); }
    }
    Console.WriteLine(bad == 0 ? "\nAll good. Run again without --dry-run to build the bundle." : $"\n{bad} problem(s). Fix the matching resolver or Catalog entry, then re-run.");
    return bad == 0 ? 0 : 1;
}

// An Apache zip you downloaded by hand. The name carries the version: httpd-2.4.66-250101-win64-VS17.zip
var local = new Dictionary<string, Package>();
var localFiles = new Dictionary<string, string>();
if (Arg("--apache-zip") is { } az)
{
    var file = Path.GetFullPath(az.Trim('"'));
    var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"httpd-(?<v>\d+\.\d+\.\d+).*win64", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!File.Exists(file) || !m.Success)
    {
        Console.Error.WriteLine($"--apache-zip: '{file}' isn't a file named like httpd-2.4.x-...-win64-VS17.zip");
        return 1;
    }
    var ver = m.Groups["v"].Value;
    local["apache"] = Catalog.Versioned("apache", VersionUtil.Series(ver), ver, "");
    localFiles["apache"] = file;
}

try
{
    var packages = await BundleBuilder.DefaultSelectionAsync(installer.Http, php, say, default, local);
    Console.WriteLine("\nBundling:");
    foreach (var p in packages) Console.WriteLine($"  {p.Name,-12} {p.Version}");
    Console.WriteLine();
    var manifest = await BundleBuilder.BuildAsync(installer, packages, outDir, say, default, localFiles);
    var bytes = manifest.Entries.Sum(e => new FileInfo(Path.Combine(outDir, e.File)).Length);
    Console.WriteLine($"\nDone: {manifest.Entries.Count} files, {bytes / 1048576.0:F0} MB in {outDir}");
    var problems = BundleCheck.Problems(outDir);
    foreach (var pr in problems) Console.Error.WriteLine("  ! " + pr);
    return problems.Count == 0 ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("\nBundle failed: " + ex.Message);
    Console.Error.WriteLine("Tip: run with --dry-run to see which vendor is the problem.");
    if (ex.Message.Contains("apachelounge", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Apache", StringComparison.OrdinalIgnoreCase))
        Console.Error.WriteLine("Apache Lounge often refuses scripted downloads. Download the Win64 zip in your browser from https://www.apachelounge.com/download/\n" +
                                "and run:  bundle.cmd --apache-zip \"C:\\path\\to\\httpd-2.4.xx-...-win64-VS17.zip\"");
    return 1;
}
