using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>Runs the generated-client Bun harnesses: type checks, the live clients against their generated bridges, and the mock harnesses.</summary>
internal sealed class GeneratedClientHarness
{
    [Test]
    public async Task GeneratedClientTypeChecksAndPassesTheBunHarnessesAsync()
    {
        var root = FindWorkspaceRoot();
        var generated = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
            "obj", "bridge-frontend", "generated");
        // Match Angular's strict consumer flags across every fixture,
        // including receipts and validation that the template may not use.
        await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "node_modules", "typescript", "bin", "tsc"),
            "--noEmit", "--strict", "--noPropertyAccessFromIndexSignature", "--noImplicitReturns",
            "--noFallthroughCasesInSwitch", "--target", "ES2022", "--module", "ESNext",
            "--moduleResolution", "bundler", "--skipLibCheck", .. Directory.GetFiles(generated, "*.ts"),
            Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests", "GeneratedTypesCheck.ts")], root)
            .ConfigureAwait(false);
        await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
            "GeneratedReconnectClientHarness.ts"), generated], root).ConfigureAwait(false);
        await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
            "GeneratedMockHarness.ts"), generated], root).ConfigureAwait(false);
    }

    // GeneratedClientHarness.ts: state, setters, checked writes, commands and validation of generated clients against
    // their generated bridges.
    [Test]
    public Task GeneratedClientsTalkToTheirGeneratedBridgesAsync() => RunLiveAsync("GeneratedClientHarness.ts");

    // GeneratedOperationClientHarness.ts: operations and interactions.
    [Test]
    [Skip("P5: needs the operation requests (start, status, watch) and interactions served")]
    public Task GeneratedOperationClientsTalkToTheirGeneratedBridgesAsync() => RunLiveAsync("GeneratedOperationClientHarness.ts");

    // Runs a live harness script against these windows.
    private static async Task RunLiveAsync(string script)
    {
        var root = FindWorkspaceRoot();
        var generated = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
            "obj", "bridge-frontend", "generated");
        var options = (string route) => new RunicWindowTestHostOptions { RootRoute = route, ViewLocator = new TestViewLocator() };
        var dataShape = new DataShapeViewModel();
        using var dataShapeHost = new RunicWindowTestHost<DataShapeViewModel>(dataShape,
            (transport, content, vm) => new DataShapeBridge(transport, vm, content: content), options("dataShape"));
        await using var context = new RunicModelContext();
        var typed = new TypedReactiveViewModel(context);
        using var contextLease = RunicModelContextRegistry.Shared.Bind(context, typed);
        using var typedHost = new RunicWindowTestHost<TypedReactiveViewModel>(typed,
            (transport, content, vm) => new TypedReactiveBridge(transport, vm, content: content), options("typedReactive"));
        using var toolkitHost = new RunicWindowTestHost<ToolkitTypedViewModel>(new ToolkitTypedViewModel(),
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content), options("toolkitTyped"));
        var failure = new FailureToolkitViewModel();
        using var failureHost = new RunicWindowTestHost<FailureToolkitViewModel>(failure,
            (transport, content, vm) => new FailureToolkitBridge(transport, vm, content: content), options("failureToolkit"));
        var validation = new ValidationViewModel();
        validation.SetErrors(null, new RunicValidationMessage("Entity failure", "entity", "error"));
        validation.Profile.SetErrors(nameof(ValidationProfile.PostalCode), new RunicValidationMessage("Invalid postal code", "postal"));
        validation.Items[0].SetErrors(nameof(ValidationItem.Name), "Missing label");
        using var validationHost = new RunicWindowTestHost<ValidationViewModel>(validation,
            (transport, content, vm) => new ValidationBridge(transport, vm, content: content), options("validation"));
        var dtoList = new DtoListViewModel();
        using var dtoListHost = new RunicWindowTestHost<DtoListViewModel>(dtoList,
            (transport, content, vm) => new DtoListBridge(transport, vm, content: content), options("dtoList"));
        // An interaction request carries its deadline as wall-clock time, which the client compares with its own clock.
        using var dtoInteraction = new DtoInteractionViewModel();
        using var dtoInteractionHost = new RunicWindowTestHost<DtoInteractionViewModel>(dtoInteraction,
            (transport, content, vm) => new DtoInteractionBridge(transport, vm, content: content), new RunicWindowTestHostOptions
            {
                RootRoute = "dtoInteraction", ViewLocator = new TestViewLocator(), TimeProvider = TimeProvider.System,
            });

        using var live = new GeneratedClientLiveHost()
            .Serve("dataShape", dataShapeHost.Transport)
            .Serve("typedReactive", typedHost.Transport)
            .Serve("toolkitTyped", toolkitHost.Transport)
            .Serve("failureToolkit", failureHost.Transport)
            .Serve("validation", validationHost.Transport)
            .Serve("dtoList", dtoListHost.Transport)
            .Serve("dtoInteraction", dtoInteractionHost.Transport)
            .Step("dataShape.expectSet", () => Require(dataShape.Duration == -new TimeSpan(1, 2, 3, 4).Add(TimeSpan.FromTicks(5_000_007))
                    && dataShape.Amount == 99.2500m && dataShape.Amount.Scale == 4
                    && dataShape.Whole is { Name: "whole-from-client", RetryAfter: 11 },
                "The generated setters' values did not reach the ViewModel as sent."))
            .Step("typedReactive.releaseSave", typed.ReleaseSave)
            .Step("failureToolkit.archive", () => failure.Title = "archived")
            .Step("dtoList.expectReplaced", () => Require(dtoList.Entries is [{ Name: "replaced", Count: 5 }],
                "The DTO list command did not reach the ViewModel."))
            .Step("dtoInteraction.expectPicked", () => Require(dtoInteraction.Picked is { Name: "picked", Count: 6 },
                "The DTO interaction output did not reach the ViewModel."));
        await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
            script), generated], root, live).ConfigureAwait(false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task AFailedHarnessChildReportsTheEndOfItsOutputAsync()
    {
        // An aborting child, like the .NET exporter after an unhandled exception,
        // prints its cause last, after more output than the retained limit.
        // Abort only once both writes are flushed: an unflushed stdout write is lost
        // with the process, which made this case fail intermittently.
        const string script = """
            process.stdout.write("x".repeat(40_000) + "\nSTDOUT_END\n", () =>
                process.stderr.write("y".repeat(40_000) + "\nSTDERR_CAUSE\n", () => process.kill(process.pid, "SIGABRT")));
            """;
        string message;
        try
        {
            await GeneratedHarnessProcess.RunAsync("bun", ["-e", script], FindWorkspaceRoot()).ConfigureAwait(false);
            throw new InvalidOperationException("The aborted harness child was reported as passing.");
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("bun generated-client harness ", StringComparison.Ordinal))
        {
            message = error.Message;
        }
        if (!message.Contains("STDERR_CAUSE", StringComparison.Ordinal) || !message.Contains("STDOUT_END", StringComparison.Ordinal)
            || !message.Contains("earlier characters omitted", StringComparison.Ordinal) || message.Length > 40_000)
            throw new InvalidOperationException($"The failure did not report the bounded end of the child's output:\n{message[..Math.Min(message.Length, 2_000)]}");
        if (!OperatingSystem.IsWindows() && !message.Contains("SIGABRT", StringComparison.Ordinal))
            throw new InvalidOperationException($"The failure did not name the abort: {message[..Math.Min(message.Length, 200)]}");
    }

    [Test]
    public async Task AHarnessChildStartsWithCoreDumpsDisabledAsync()
    {
        // The kernel keeps an aborting child alive while it writes a core dump, which
        // outlasts the harness limit on a loaded runner and hides the child's cause.
        // Linux skips a piped core handler only for a limit of exactly 1 byte.
        if (OperatingSystem.IsWindows()) return;
        const string script = """
            const limit = process.platform === "linux"
              ? require("node:fs").readFileSync("/proc/self/limits", "utf8").split("\n")
                  .find((line) => line.startsWith("Max core file size")).split(/\s+/)[4]
              : Bun.spawnSync(["sh", "-c", "ulimit -c"]).stdout.toString().trim();
            process.stdout.write(`CORE_LIMIT=${limit}\n`);
            process.exit(1);
            """;
        // A shell that ran 'ulimit -c 0' leaves a hard limit of 0, which cannot be raised.
        var expected = OperatingSystem.IsLinux() && CoreHardLimit() != "0" ? "CORE_LIMIT=1\n" : "CORE_LIMIT=0\n";
        try
        {
            await GeneratedHarnessProcess.RunAsync("bun", ["-e", script], FindWorkspaceRoot()).ConfigureAwait(false);
            throw new InvalidOperationException("The failing harness child was reported as passing.");
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("bun generated-client harness ", StringComparison.Ordinal))
        {
            if (!error.Message.Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"The harness child could still write a core dump:\n{error.Message}");
        }
    }

    private static string CoreHardLimit() => File.ReadLines("/proc/self/limits")
        .First(line => line.StartsWith("Max core file size", StringComparison.Ordinal))
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)[5];

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }
}
