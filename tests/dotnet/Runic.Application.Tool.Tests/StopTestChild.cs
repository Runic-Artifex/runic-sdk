using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Runic.Application.Tool;

namespace Runic.Application.Tool.Tests;

/// <summary>
/// A child of the test runner that stands in for <c>dotnet watch</c> ("parent", which ignores signals) and the
/// application under <c>dotnet run</c> ("run"). The "run" role exits with 3 on its first signal unless a "stuck"
/// file exists in the marker directory; stuck, it writes a "signalled" marker instead.
/// </summary>
internal static partial class StopTestChild
{
    internal const string Argument = "__stop-test-child";
    internal const int ApplicationExitCode = 3;

    /// <summary>Stands in for a dev server that lost its port: prints Vite's message and exits.</summary>
    internal const string PortInUseRole = "port-in-use";

    internal static int Run(string role, string markers)
    {
        if (role == PortInUseRole)
        {
            Console.Error.WriteLine("error when starting dev server:");
            Console.Error.WriteLine("Error: Port 5173 is already in use");
            return 1;
        }

        if (OperatingSystem.IsWindows())
        {
            // The test runner's own host may have disabled Ctrl+C for its children; an application has it enabled.
            _ = SetConsoleCtrlHandler(0, false);
        }

        bool stuck = role != "run" || File.Exists(Path.Combine(markers, "stuck"));
        using var stopped = new ManualResetEventSlim();
        void Handle(PosixSignalContext context)
        {
            context.Cancel = true;
            if (!stuck) stopped.Set();
            // The test knows then that the stop is waiting out its grace period.
            else if (role == "run") File.WriteAllText(Path.Combine(markers, "signalled"), "");
        }
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle);

        if (role == "parent")
        {
            (string executable, var prefix) = ProcessTreeSignal.SelfCommand();
            var startInfo = new ProcessStartInfo(executable);
            foreach (string argument in prefix) startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add(Argument);
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(markers);
            using Process child = Process.Start(startInfo)!;
            Thread.Sleep(Timeout.Infinite);
        }

        string ready = Path.Combine(markers, "ready");
        File.WriteAllText(ready + ".tmp", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(ready + ".tmp", ready);
        stopped.Wait();
        File.WriteAllText(Path.Combine(markers, "stopped"), "");
        return ApplicationExitCode;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
