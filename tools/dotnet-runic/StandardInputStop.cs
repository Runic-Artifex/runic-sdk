using System;
using System.IO;
using System.Threading;

namespace Runic.Application.Tool;

/// <summary>
/// WIN-06: a stop request on <c>dotnet runic dev</c>'s standard input, for an IDE that started <c>dev</c> and cannot
/// signal it. On Windows, Visual Studio has no Ctrl+C to send a process without a console and ends the tree with
/// <c>taskkill /F</c>, so <c>dev</c> never ran its stop handler (session file, frontend marker, development pages).
/// With <see cref="Variable"/> set to <c>1</c>, <c>dev</c> reads its standard input and stops as on Ctrl+C at a line
/// <see cref="StopLine"/> or at the end of the input, when the IDE closes the pipe. Other lines are ignored. Opt-in, so
/// a terminal's input is left alone; with it, the processes that <c>dev</c> starts get an empty input of their own, so
/// none of them reads the request.
/// </summary>
internal static class StandardInputStop
{
    internal const string Variable = "RUNIC_DEV_STOP_ON_INPUT";
    internal const string StopLine = "stop";

    internal const string StopLineMessage = "[dev] Standard input asked dev to stop; stopping the application.";
    internal const string EndMessage = "[dev] Standard input ended; stopping the application.";

    /// <summary>Whether the environment asks for the stop request: <c>1</c> or <c>true</c>, without case.</summary>
    internal static bool IsRequested(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? value = environment(Variable)?.Trim();
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether processes that dev starts get an input of their own (<see cref="RunningProcess"/>): set once, before
    /// dev starts any, when the stop request is on.
    /// </summary>
    internal static bool HoldsChildInput { get; set; }

    /// <summary>
    /// Watches <paramref name="input"/> on a background thread, as a console read cannot be cancelled; the thread ends
    /// with the process.
    /// </summary>
    internal static void Start(TextReader input, Action<string> stopRequested)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(stopRequested);
        var thread = new Thread(() => Watch(input, stopRequested))
        {
            IsBackground = true,
            Name = "dotnet runic dev standard input",
        };
        thread.Start();
    }

    /// <summary>
    /// Reads <paramref name="input"/> until a <see cref="StopLine"/> or its end and then calls
    /// <paramref name="stopRequested"/> once, with the line dev prints.
    /// </summary>
    internal static void Watch(TextReader input, Action<string> stopRequested)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(stopRequested);
        try
        {
            while (input.ReadLine() is { } line)
            {
                if (string.Equals(line.Trim(), StopLine, StringComparison.OrdinalIgnoreCase))
                {
                    stopRequested(StopLineMessage);
                    return;
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // A broken pipe ends the input as closing it does.
        }
        stopRequested(EndMessage);
    }
}
