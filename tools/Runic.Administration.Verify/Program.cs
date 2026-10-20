using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json.Serialization;
using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Spectre;
using Runic.Platform.Administration.Windows;

[assembly: SupportedOSPlatform("windows")]

// Fixture entry points started by the Service Control Manager and Task Scheduler.
// They are process contracts of the write suite, not part of the command surface.
if (args is ["--service", var serviceName]) return ServiceFixture.Run(serviceName);
if (args is ["--task-marker", var marker]) { File.WriteAllText(marker, "Runic task completed"); return 0; }
return await VerifierApplication.RunAsync(args);

internal static class VerifierApplication
{
    internal const int ChecksPassed = 0;
    internal const int ChecksFailed = 1;
    internal const int UsageOrPlatform = 2;
    private static readonly string[] LocalCapabilities = ["shortcuts", "services", "tasks", "firewall", "shares", "system", "processes", "networks"];
    private static readonly string[] DomainCapabilities = ["ldap", "gpo", "dns"];
    private static readonly string[] DeniedCapabilities = ["services", "tasks", "firewall", "shares"];

    internal static async Task<int> RunAsync(string[] args)
    {
        // The first Ctrl+C requests cancellation and keeps the process alive, so the
        // cleanup of created resources still runs. CommandApp's own handler would
        // terminate on a second Ctrl+C and skip that cleanup.
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return await CreateApplication().RunAsync(args, cancellation.Token); }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static CommandApp CreateApplication() => new(GeneratedCommandCatalog.Create())
    {
        Name = "Runic.AdminVerify",
        Version = typeof(WindowsAdministrationException).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(WindowsAdministrationException).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        HandleCancelKeyPress = false,
        HelpPresenter = new SpectreHelpPresenter(),
        Console = new SpectreCommandConsole(),
        ExitCodePolicy = VerifierExitCodePolicy.Instance,
        OutcomeSink = VerifierOutcomeSink.Instance,
    };

    [Command("local",
        Description = "Verify local Windows administration. Read-only inspection by default; writes need an explicit opt-in on a disposable VM. Ctrl+C requests cancellation; cleanup still runs, but a blocking native call can finish first.",
        Examples =
        [
            "Runic.AdminVerify local",
            "Runic.AdminVerify local --allow-changes --only services,tasks,firewall,shares",
            "Runic.AdminVerify local --expect-denied --only services,tasks,firewall,shares",
        ])]
    [CommandResult("runic.administration.verify/1", typeof(VerifierJsonContext))]
    internal static Task<CommandOutcome<VerificationSummary>> Local(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--allow-changes", Description = "Create, update and delete owned services, tasks, firewall rules and shares. Elevated terminal on a snapshotted VM only.")] bool allowChanges = false,
        [Option("--expect-denied", Description = "From a non-elevated process, attempt the owned writes and require Windows to deny each one.")] bool expectDenied = false,
        [Option("--only", ValueName = "CAPABILITIES", Description = "Comma-separated subset: shortcuts, services, tasks, firewall, shares, system, processes, networks.")] string? only = null,
        [Option("--out", ValueName = "DIRECTORY", Description = "Parent directory for the run's report folder (default ./runic-results).")] string? output = null)
    {
        if (Select(only, LocalCapabilities) is not { } selected)
            return Usage("RAV1001", "Invalid --only selection for the local suite.");
        if (allowChanges && expectDenied)
            return Usage("RAV1002", "Choose either --allow-changes or --expect-denied.");
        if (Blank(output)) return Usage("RAV1003", "Empty --out value.");
        if (expectDenied && selected.Count != 0 && !selected.Overlaps(DeniedCapabilities))
            return Usage("RAV1007", "--expect-denied needs at least one of services, tasks, firewall or shares in --only.");
        if (expectDenied && OperatingSystem.IsWindows() && IsElevated())
            return Usage("RAV1004", "--expect-denied must run from a non-elevated process; an elevated run would create the resources.");
        return RunAsync(context, new Options("local", allowChanges, expectDenied, FullPath(output), null, null, null, null, null, selected), cancellationToken);
    }

    [Command("domain",
        Description = "Verify LDAP, Group Policy and DNS against a disposable test domain. Read-only unless --allow-changes is given with an existing --base-dn. Ctrl+C requests cancellation; cleanup still runs, but a blocking native call can finish first.",
        Examples =
        [
            "Runic.AdminVerify domain --server dc1.example.test --domain example.test",
            "Runic.AdminVerify domain --server dc1.example.test --domain example.test --base-dn \"OU=RunicTests,DC=example,DC=test\" --dns-zone example.test --allow-changes",
            "Runic.AdminVerify domain --server dc1.example.test --domain example.test --base-dn \"OU=RunicTests,DC=example,DC=test\" --dns-zone example.test --expect-denied --credential-stdin",
        ])]
    [CommandResult("runic.administration.verify/1", typeof(VerifierJsonContext))]
    internal static Task<CommandOutcome<VerificationSummary>> Domain(
        CommandExecutionContext context,
        [Option("--server", ValueName = "DC", Description = "Domain controller for LDAP and Group Policy.")] string server,
        [Option("--domain", ValueName = "DNS-NAME", Description = "DNS name of the test domain.")] string domain,
        CancellationToken cancellationToken,
        [Option("--base-dn", ValueName = "DN", Description = "Existing disposable OU; writes create a child OU below it.")] string? baseDn = null,
        [Option("--dns-server", ValueName = "HOST", Description = "DNS server, when separate from the controller.")] string? dnsServer = null,
        [Option("--dns-zone", ValueName = "ZONE", Description = "Existing test zone for DNS record checks; no zone is created.")] string? dnsZone = null,
        [Option("--allow-changes", Description = "Create and delete owned directory objects, GPOs and DNS records.")] bool allowChanges = false,
        [Option("--expect-denied", Description = "Attempt owned LDAP, DNS and GPO writes below --base-dn and in --dns-zone with a restricted identity, and require each to be denied.")] bool expectDenied = false,
        [Option("--credential-stdin", Description = "Read a user name (DOMAIN\\user or UPN) and a password as two lines from standard input. LDAP and DNS receive them as explicit credentials; GPMC runs under a network-only logon with them.")] bool credentialStdin = false,
        [Option("--only", ValueName = "CAPABILITIES", Description = "Comma-separated subset: ldap, gpo, dns.")] string? only = null,
        [Option("--out", ValueName = "DIRECTORY", Description = "Parent directory for the run's report folder (default ./runic-results).")] string? output = null)
    {
        if (Select(only, DomainCapabilities) is not { } selected)
            return Usage("RAV1001", "Invalid --only selection for the domain suite.");
        if (Blank(server) || Blank(domain) || Blank(baseDn) || Blank(dnsServer) || Blank(dnsZone) || Blank(output))
            return Usage("RAV1003", "Option values must not be empty.");
        if (allowChanges && expectDenied)
            return Usage("RAV1002", "Choose either --allow-changes or --expect-denied.");
        if ((allowChanges || expectDenied) && baseDn is null)
            return Usage("RAV1005", "Domain writes require an explicit existing --base-dn.");
        // An interactive console would echo the password, so the credential must be piped in.
        if (credentialStdin && !Console.IsInputRedirected)
            return Usage("RAV1009", "--credential-stdin reads from redirected standard input only; pipe the user name and password in so that the console never echoes them.");
        var credential = credentialStdin ? ReadCredential() : null;
        if (credentialStdin && credential is null)
            return Usage("RAV1008", "--credential-stdin needs a user name line and a non-empty password line on standard input.");
        return RunAsync(context, new Options("domain", allowChanges, expectDenied, FullPath(output), server, domain, baseDn, dnsServer, dnsZone, selected) { Credential = credential }, cancellationToken);
    }

    private static async Task<CommandOutcome<VerificationSummary>> RunAsync(CommandExecutionContext context, Options options, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return CommandOutcome.Failure<VerificationSummary>(CommandExitCategory.Unavailable, new CommandFault("RAV1006", "Windows x64 is required."));
        // Progress goes through the invocation console, so JSON output keeps stdout for its envelope.
        void Write(string line) => context.Console.WriteOutAsync((line + Environment.NewLine).AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var report = new RunReport(options, Write);
        Write($"Run {report.Id}; reports: {report.Folder}");
        Write("Windows capabilities use generated bindings; LDAP uses System.DirectoryServices.Protocols.");
        if (options.Credential is { } identity) Write("Domain identity: " + (identity.Domain.Length == 0 ? identity.UserName : identity.Domain + "\\" + identity.UserName) + " (explicit credential).");
        Write(options.Changes ? "Administrative fixture writes ENABLED."
            : options.ExpectDenied ? (options.Suite == "domain" ? "Restricted-identity write attempts ENABLED; each must be denied." : "Non-elevated write attempts ENABLED; each must be denied.")
            : "Administrative inspection only.");
        try
        {
            if (options.Suite == "local") await new LocalChecks(report, options, token).RunAsync();
            else await new DomainChecks(report, options, token).RunAsync();
        }
        catch (Exception error) { report.Record("runner", "FAIL", error); }
        report.Finish();
        var summary = new VerificationSummary(report.Id, options.Suite, options.Changes, options.ExpectDenied, report.Folder,
            Count("PASS"), Count("FAIL"), Count("SKIP"), Count("CANCELED"));
        Write($"Finished: {summary.Passed} passed; {summary.Failed} failed; {summary.Skipped} skipped.");
        Write($"Report: {Path.Combine(report.Folder, "report.txt")}");
        if (summary.Canceled > 0 || token.IsCancellationRequested)
            return CommandOutcome.Failure<VerificationSummary>(CommandExitCategory.Cancelled, new CommandFault("RAV2002", "The run was canceled; inspect the report and the resource journal."));
        if (summary.Failed > 0)
            return CommandOutcome.Failure<VerificationSummary>(CommandExitCategory.CommandFailure, new CommandFault("RAV2001", "One or more selected checks failed; see the report."));
        return CommandOutcome.Success(summary);
        int Count(string status) => report.Results.Count(r => r.Status == status);
    }

    private static HashSet<string>? Select(string? only, string[] capabilities)
    {
        if (only is null) return [];
        var selected = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        return selected.Count == 0 || selected.Any(value => !capabilities.Contains(value, StringComparer.Ordinal)) ? null : selected;
    }
    // The password stays in memory only; it is never echoed, logged or written to the report.
    // Only the user name appears in the progress output.
    private static NetworkCredential? ReadCredential()
    {
        var user = Console.In.ReadLine()?.Trim();
        var password = Console.In.ReadLine();
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)) return null;
        var separator = user.IndexOf('\\', StringComparison.Ordinal);
        return separator > 0 ? new NetworkCredential(user[(separator + 1)..], password, user[..separator]) : new NetworkCredential(user, password);
    }
    private static bool Blank(string? value) => value is not null && string.IsNullOrWhiteSpace(value);
    private static string FullPath(string? output) => Path.GetFullPath(output ?? "runic-results");
    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static Task<CommandOutcome<VerificationSummary>> Usage(string code, string message) =>
        Task.FromResult(CommandOutcome.Failure<VerificationSummary>(CommandExitCategory.Validation, new CommandFault(code, message)));

    private sealed class VerifierExitCodePolicy : IExitCodePolicy
    {
        public static VerifierExitCodePolicy Instance { get; } = new();
        public int GetExitCode(CommandExitCategory category) => category switch
        {
            CommandExitCategory.Success => ChecksPassed,
            CommandExitCategory.CommandFailure or CommandExitCategory.Cancelled or CommandExitCategory.HostFailure => ChecksFailed,
            CommandExitCategory.Usage or CommandExitCategory.Validation or CommandExitCategory.Unavailable => UsageOrPlatform,
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
    }

    // Human output: the progress, summary and report path are already written; only
    // report a fault. JSON output keeps the standard versioned envelope.
    private sealed class VerifierOutcomeSink : ICommandOutcomeSink
    {
        public static VerifierOutcomeSink Instance { get; } = new();
        public ValueTask WriteAsync<TResult>(CommandDescriptor command, CommandExecutionContext context, CommandOutcome<TResult> outcome,
            ICommandResultCodec<TResult> codec, int exitCode, IReadOnlyList<CommandDiagnostic> diagnostics, CancellationToken cancellationToken)
        {
            if (context.OutputMode == CommandOutputMode.Json)
                return new CommandOutputDispatcher().WriteAsync(command, context, outcome, codec, exitCode, diagnostics, cancellationToken);
            return outcome.Fault is { } fault
                ? context.Console.WriteErrorAsync((fault.Message + Environment.NewLine).AsMemory(), cancellationToken)
                : ValueTask.CompletedTask;
        }
    }
}

internal sealed record Options(string Suite, bool Changes, bool ExpectDenied, string Output, string? Server, string? Domain,
    string? BaseDn, string? DnsServer, string? DnsZone, HashSet<string> Only)
{
    /// <summary>Explicit domain identity from --credential-stdin; null uses the process identity.</summary>
    internal NetworkCredential? Credential { get; init; }
    internal bool Includes(string capability) => Only.Count == 0 || Only.Contains(capability);
}

// The runic.administration.verify/1 payload names its properties in PascalCase, spelled out
// so that the JSON context's options cannot rename them.
internal sealed record VerificationSummary(
    [property: JsonPropertyName("RunId")] string RunId,
    [property: JsonPropertyName("Suite")] string Suite,
    [property: JsonPropertyName("ChangesEnabled")] bool ChangesEnabled,
    [property: JsonPropertyName("ExpectDenied")] bool ExpectDenied,
    [property: JsonPropertyName("ReportFolder")] string ReportFolder,
    [property: JsonPropertyName("Passed")] int Passed,
    [property: JsonPropertyName("Failed")] int Failed,
    [property: JsonPropertyName("Skipped")] int Skipped,
    [property: JsonPropertyName("Canceled")] int Canceled);

[JsonSerializable(typeof(VerificationSummary))]
internal sealed partial class VerifierJsonContext : JsonSerializerContext;
