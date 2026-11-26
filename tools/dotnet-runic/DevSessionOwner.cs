using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Runic.Application.Tool.WebKit;

namespace Runic.Application.Tool;

internal sealed partial class DevSession
{
    /// <summary>How far apart two reports of one process's start time may be: APIs differ in precision (README, Session file).</summary>
    internal static readonly TimeSpan ProcessStartTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// sdk#1405: before its first build, dev removes the frontend marker (and the development pages beside it) of a dev
    /// that no longer runs. A killed dev cannot remove its marker, and a build within <see cref="FrontendMarkerLifetime"/>
    /// of its last renewal takes the session for a running one: it skips the frontend build and, for a frontend that
    /// only has a watcher, the install, which the build owns since 0.24. A marker of a dev that still runs stays, so
    /// builds that dev did not start keep that session's frontend as before. Returns the <c>[dev]</c> line to print,
    /// or null when there was nothing to remove.
    /// </summary>
    /// <param name="sessionDirectory">The session file's directory, <c>obj/runic</c>.</param>
    /// <param name="processStartTime">A process's start time, or null when no process has that ID.</param>
    /// <param name="commandLine">A process's arguments, or null when they cannot be read.</param>
    internal static string? RemoveStaleFrontendMarker(string sessionDirectory,
        Func<int, DateTimeOffset?> processStartTime, Func<int, IReadOnlyList<string>?> commandLine)
    {
        ArgumentNullException.ThrowIfNull(sessionDirectory);
        string marker = System.IO.Path.Combine(sessionDirectory, FrontendMarkerName);
        if (!File.Exists(marker)) return null;
        (bool runs, int? owner) = SessionRuns(System.IO.Path.Combine(sessionDirectory, FileName), processStartTime, commandLine);
        if (runs) return null;
        string ownerText = owner is { } pid ? $" (process {pid.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        try
        {
            // The marker first, as at a stop: a build that starts now runs the frontend build and copy again.
            File.Delete(marker);
            string pages = System.IO.Path.Combine(sessionDirectory, PagesDirectoryName);
            if (Directory.Exists(pages)) Directory.Delete(pages, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"[dev] Could not remove {marker}, which a dotnet runic dev that no longer runs left{ownerText}: {error.Message} " +
                "Builds within 30 seconds of its last renewal skip the frontend build and install; delete the file if this dev's frontend has no packages.";
        }
        return $"[dev] Removed {marker}, which a dotnet runic dev that no longer runs left{ownerText}, so this dev's build installs and builds the frontend.";
    }

    /// <summary><see cref="RemoveStaleFrontendMarker(string, Func{int, DateTimeOffset?}, Func{int, IReadOnlyList{string}?})"/> for this machine's processes.</summary>
    internal static string? RemoveStaleFrontendMarker(string sessionDirectory) =>
        RemoveStaleFrontendMarker(sessionDirectory, ProcessStartTime, ProcessCommandLine);

    /// <summary>
    /// Whether the session file names a dev that runs, as tools/dotnet-runic/README.md tells a reader to check: its state
    /// is not <c>stopped</c>, a process with <c>processId</c> runs and started at <c>processStartedAt</c>, to within
    /// <see cref="ProcessStartTolerance"/>. A file without <c>processStartedAt</c> (before 0.20) names a dev when the
    /// process's command line runs <c>dotnet runic dev</c> (<see cref="PortHolder.IsDevCommandLine"/>), or when its
    /// command line cannot be read. A missing or unreadable file names none.
    /// </summary>
    internal static (bool Runs, int? ProcessId) SessionRuns(string sessionFile,
        Func<int, DateTimeOffset?> processStartTime, Func<int, IReadOnlyList<string>?> commandLine)
    {
        ArgumentNullException.ThrowIfNull(processStartTime);
        ArgumentNullException.ThrowIfNull(commandLine);
        int processId;
        string? state, recorded;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(sessionFile));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("processId", out JsonElement id) || !id.TryGetInt32(out processId)) return (false, null);
            state = root.TryGetProperty("state", out JsonElement stateValue) && stateValue.ValueKind == JsonValueKind.String ? stateValue.GetString() : null;
            recorded = root.TryGetProperty("processStartedAt", out JsonElement started) && started.ValueKind == JsonValueKind.String ? started.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (false, null);
        }
        if (state == Stopped) return (false, processId);
        if (processStartTime(processId) is not { } startedAt) return (false, processId);
        if (recorded is not null)
        {
            return DateTimeOffset.TryParse(recorded, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset expected)
                ? ((startedAt - expected).Duration() <= ProcessStartTolerance, processId)
                : (false, processId);
        }
        return (commandLine(processId) is not { } arguments || PortHolder.IsDevCommandLine(arguments), processId);
    }

    /// <summary>The start time of the process with <paramref name="processId"/>, or null when none runs.</summary>
    internal static DateTimeOffset? ProcessStartTime(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // No such process, one that exited meanwhile, or another user's that cannot be read: not this user's dev.
            return null;
        }
    }

    /// <summary>The process's arguments on Linux (<c>/proc/&lt;pid&gt;/cmdline</c>); null elsewhere or when unreadable.</summary>
    private static IReadOnlyList<string>? ProcessCommandLine(int processId)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            string text = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/cmdline");
            return text.Length == 0 ? null : text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
