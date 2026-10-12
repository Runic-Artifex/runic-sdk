using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>Runs the Bun harness against a transcript emitted by this built test application.</summary>
internal sealed class GeneratedClientHarness
{
    [Test]
    public async Task GeneratedClientTypeChecksAndPassesTheBunHarnessesAsync()
    {
        var root = FindWorkspaceRoot();
        var generated = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
            "obj", "bridge-frontend", "generated");
        var fixture = Path.Combine(Path.GetTempPath(), $"runic-generated-client-{Guid.NewGuid():N}.json");
        try
        {
            // Match Angular's strict consumer flags across every fixture,
            // including receipts and validation that the template may not use.
            await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "node_modules", "typescript", "bin", "tsc"),
                "--noEmit", "--strict", "--noPropertyAccessFromIndexSignature", "--noImplicitReturns",
                "--noFallthroughCasesInSwitch", "--target", "ES2022", "--module", "ESNext",
                "--moduleResolution", "bundler", "--skipLibCheck", .. Directory.GetFiles(generated, "*.ts"),
                Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests", "GeneratedTypesCheck.ts")], root)
                .ConfigureAwait(false);
            await GeneratedHarnessProcess.RunAsync("dotnet", [typeof(GeneratedClientHarness).Assembly.Location,
                "--export-generated-client-fixture", fixture], root).ConfigureAwait(false);
            await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
                "GeneratedClientHarness.ts"), fixture, generated], root).ConfigureAwait(false);
            await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
                "GeneratedReconnectClientHarness.ts"), generated], root).ConfigureAwait(false);
            await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
                "GeneratedMockHarness.ts"), generated], root).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(fixture); }
            catch (IOException) { }
        }
    }

    [Test]
    public async Task AFailedHarnessChildReportsTheEndOfItsOutputAsync()
    {
        // An aborting child, like the .NET exporter after an unhandled exception,
        // prints its cause last, after more output than the retained limit.
        const string script = """
            process.stdout.write("x".repeat(40_000) + "\nSTDOUT_END\n");
            process.stderr.write("y".repeat(40_000) + "\nSTDERR_CAUSE\n", () => process.kill(process.pid, "SIGABRT"));
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
