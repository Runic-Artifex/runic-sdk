using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

if (args is ["--help"] or ["-h"] or [])
{
    Console.WriteLine("""
Runic Windows Administration verification (Windows x64, NativeAOT)
  Runic.AdminVerify local [--allow-changes] [--only shortcuts,services,tasks,firewall,shares,system,processes,networks]
  Runic.AdminVerify domain --server DC --domain example.test [--base-dn "OU=Tests,DC=example,DC=test"]
      [--dns-server DNS] [--dns-zone example.test] [--allow-changes] [--only ldap,gpo,dns]
  Common: --out DIRECTORY (default ./runic-results)

Without --allow-changes: inspection only, plus owned temporary shortcut files.
With --allow-changes: use ONLY a disposable VM/domain fixture. Take a VM snapshot first.
Domain writes require an explicit existing --base-dn and use a new child OU.
Uses the current Windows identity. Run an elevated terminal for local writes.
No elevation, credentials on the command line, existing-resource replacement or global settings changes.
Each check and cleanup is recorded in report.json and report.txt.
Exit: 0 selected checks passed (inspect SKIPs), 1 failure/canceled, 2 usage/platform error.
Ctrl+C requests cancellation; native calls may finish before cleanup can run.
""");
    return 0;
}
if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    Console.Error.WriteLine("Windows x64 is required."); return 2;
}
if (args is ["--service", var serviceName]) return ServiceFixture.Run(serviceName);
if (args is ["--task-marker", var marker]) { File.WriteAllText(marker, "Runic task completed"); return 0; }

Options options;
try { options = Options.Parse(args); }
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); Console.Error.WriteLine("Use --help for usage."); return 2; }
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var report = new RunReport(options);
Console.WriteLine($"Run {report.Id}; reports: {report.Folder}");
Console.WriteLine(options.Changes ? "Administrative fixture writes ENABLED." : "Administrative inspection only.");
try
{
    if (options.Suite == "local") await new LocalChecks(report, options, cancellation.Token).RunAsync();
    else await new DomainChecks(report, options, cancellation.Token).RunAsync();
}
catch (Exception error) { report.Record("runner", "FAIL", error); }
report.Finish();
Console.WriteLine($"Finished: {report.Results.Count(r => r.Status == "PASS")} passed; {report.Results.Count(r => r.Status == "FAIL")} failed; {report.Results.Count(r => r.Status == "SKIP")} skipped.");
Console.WriteLine($"Report: {Path.Combine(report.Folder, "report.txt")}");
return report.Results.Any(r => r.Status is "FAIL" or "CANCELED") || cancellation.IsCancellationRequested ? 1 : 0;

internal sealed record Options(string Suite, bool Changes, string Output, string? Server, string? Domain,
    string? BaseDn, string? DnsServer, string? DnsZone, HashSet<string> Only)
{
    internal bool Includes(string capability) => Only.Count == 0 || Only.Contains(capability);
    internal static Options Parse(string[] args)
    {
        if (args[0] is not ("local" or "domain")) throw new ArgumentException("Choose local or domain.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var changes = false;
        var allowed = new[] { "--out", "--server", "--domain", "--base-dn", "--dns-server", "--dns-zone", "--only" };
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--allow-changes") { if (changes) throw new ArgumentException("Duplicate --allow-changes."); changes = true; continue; }
            var name = args[i];
            if (!allowed.Contains(name, StringComparer.Ordinal) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unknown option or missing value: " + name);
            if (!values.TryAdd(name, args[++i]) || string.IsNullOrWhiteSpace(args[i])) throw new ArgumentException("Duplicate or empty option: " + name);
        }
        string? Get(string key) => values.GetValueOrDefault(key);
        var capabilities = args[0] == "local" ? new[] { "shortcuts", "services", "tasks", "firewall", "shares", "system", "processes", "networks" } : ["ldap", "gpo", "dns"];
        var only = (Get("--only") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        if (values.ContainsKey("--only") && only.Count == 0 || only.Any(value => !capabilities.Contains(value, StringComparer.Ordinal)))
            throw new ArgumentException("Invalid --only selection for this suite.");
        if (args[0] == "local" && values.Keys.Any(key => key is not ("--out" or "--only"))) throw new ArgumentException("Domain target options cannot be used with local.");
        if (args[0] == "domain" && (Get("--server") is null || Get("--domain") is null)) throw new ArgumentException("Domain suite requires --server and --domain.");
        if (args[0] == "domain" && changes && Get("--base-dn") is null) throw new ArgumentException("Domain writes require an explicit existing --base-dn.");
        return new(args[0], changes, Path.GetFullPath(Get("--out") ?? "runic-results"), Get("--server"), Get("--domain"), Get("--base-dn"), Get("--dns-server"), Get("--dns-zone"), only);
    }
}
