using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>
/// Stops <c>dotnet runic dev</c> when the <c>dotnet</c> command that started it exits.
/// </summary>
/// <remarks>
/// <c>dotnet runic</c> runs the tool as a child of the <c>dotnet</c> CLI process, which forwards SIGTERM to it from an
/// <c>AppDomain.ProcessExit</c> handler. The .NET 11 SDK runs the CLI through a NativeAOT entry point that registers no
/// SIGTERM handler, and since .NET 10 the runtime has none by default: the CLI dies on SIGTERM without raising
/// ProcessExit, so the tool is never signalled and keeps the development server, <c>dotnet watch</c> and the application
/// running (D059.17). On Unix, dev therefore treats the exit of its <c>dotnet</c> parent as a stop request. On Windows,
/// the CLI's job object ends the tool with it.
/// </remarks>
internal static partial class LauncherWatch
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Calls <paramref name="launcherExited"/> once the <c>dotnet</c> process that started this one exits, or does
    /// nothing on Windows and when another program, such as a shell, started it.
    /// </summary>
    internal static Task Start(Action launcherExited, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launcherExited);
        if (OperatingSystem.IsWindows()) return Task.CompletedTask;
        int launcher = GetParentProcessId();
        if (launcher <= 1 || !IsDotNetHost(ProcessName(launcher))) return Task.CompletedTask;
        return WatchAsync(GetParentProcessId, launcher, PollInterval, launcherExited, TimeProvider.System, cancellationToken);
    }

    /// <summary>
    /// Polls the parent process ID, which changes when the launcher exits and this process is reparented, and calls
    /// <paramref name="launcherExited"/> once it does.
    /// </summary>
    internal static async Task WatchAsync(Func<int> parentProcessId, int launcher, TimeSpan interval,
        Action launcherExited, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (parentProcessId() == launcher) continue;
                launcherExited();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Whether a process name is the dotnet host, which runs <c>dotnet runic</c> and <c>dotnet tool run</c>.</summary>
    internal static bool IsDotNetHost(string? processName) =>
        StringComparer.OrdinalIgnoreCase.Equals(processName, "dotnet");

    private static string? ProcessName(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int GetParentProcessId();
}
