// Shared by the test and smoke projects that set RunicTestTimeouts=true (eng/build/test-timeouts.targets).
// The rules are in eng/ci/README.md, "Test timeouts".
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Scales the waits for a condition (a window connects, a file appears, a frame arrives, a process exits) by
/// <c>RUNIC_TEST_TIMEOUT_SCALE</c>, a positive number that defaults to 1. CI sets it per runner, so a slow runner
/// gets more time while a passing wait still ends as soon as its condition holds. Product timing (a deadline that
/// fires after X, debounce or interval behaviour) and performance budgets are not scaled.
/// </summary>
/// <remarks>
/// A scaled wait that succeeds only after its unscaled time reports a warning, as a GitHub Actions
/// <c>::warning::</c> annotation in CI, naming the caller and the elapsed against the unscaled time.
/// </remarks>
internal static class TestTimeouts
{
    public const string Variable = "RUNIC_TEST_TIMEOUT_SCALE";

    private static readonly ConcurrentDictionary<string, bool> s_reported = new(StringComparer.Ordinal);
    private static readonly object s_outputLock = new();

    /// <summary>The configured factor; 1 when <c>RUNIC_TEST_TIMEOUT_SCALE</c> is not set.</summary>
    public static double Factor { get; } = ReadFactor(Environment.GetEnvironmentVariable(Variable));

    internal static double ReadFactor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 1;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) && double.IsFinite(factor) && factor > 0)
            return factor;
        throw new InvalidOperationException($"{Variable} must be a positive number, got '{value}'.");
    }

    /// <summary>The scaled time for a condition wait whose unscaled limit is <paramref name="unscaled"/>.</summary>
    public static TimeSpan Scale(TimeSpan unscaled)
    {
        if (unscaled == Timeout.InfiniteTimeSpan || unscaled <= TimeSpan.Zero || Factor == 1) return unscaled;
        var ticks = Math.Ceiling(unscaled.Ticks * Factor);
        // Task.WaitAsync and CancellationTokenSource accept at most uint.MaxValue - 1 milliseconds.
        return ticks >= TimeSpan.FromMilliseconds(uint.MaxValue - 1).Ticks ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>The scaled milliseconds for an API that takes an <see cref="int"/> timeout.</summary>
    public static int Scale(int milliseconds) =>
        milliseconds <= 0 ? milliseconds : (int)Math.Min(int.MaxValue, Math.Ceiling(milliseconds * Factor));

    /// <summary>A cancellation source that cancels after the scaled <paramref name="unscaled"/> limit.</summary>
    public static CancellationTokenSource Cancellation(TimeSpan unscaled) => new(Scale(unscaled));

    /// <summary>Starts timing a condition wait; see <see cref="ScaledWait"/>.</summary>
    public static ScaledWait Start(TimeSpan unscaled, [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => new(unscaled, caller, file, line);

    /// <summary><see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/> with the scaled limit.</summary>
    public static async Task WaitScaledAsync(this Task task, TimeSpan unscaled, CancellationToken cancellationToken = default,
        [CallerMemberName] string caller = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var wait = new ScaledWait(unscaled, caller, file, line);
        try { await task.WaitAsync(wait.Limit, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException error) when (!task.IsCompleted) { throw wait.Expired(error); }
        wait.Succeeded();
    }

    /// <summary><see cref="Task{TResult}.WaitAsync(TimeSpan, CancellationToken)"/> with the scaled limit.</summary>
    public static async Task<T> WaitScaledAsync<T>(this Task<T> task, TimeSpan unscaled, CancellationToken cancellationToken = default,
        [CallerMemberName] string caller = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var wait = new ScaledWait(unscaled, caller, file, line);
        T result;
        try { result = await task.WaitAsync(wait.Limit, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException error) when (!task.IsCompleted) { throw wait.Expired(error); }
        wait.Succeeded();
        return result;
    }

    /// <summary><see cref="Process.WaitForExit(TimeSpan)"/> with the scaled limit.</summary>
    public static bool WaitForExitScaled(this Process process, TimeSpan unscaled, [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var wait = new ScaledWait(unscaled, caller, file, line);
        if (!process.WaitForExit(wait.Limit)) return false;
        wait.Succeeded();
        return true;
    }

    /// <summary>
    /// Polls <paramref name="condition"/> every <paramref name="interval"/> (default 25 ms) until it holds or the
    /// scaled limit passes, and returns whether it held.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan unscaled, TimeSpan? interval = null,
        CancellationToken cancellationToken = default, [CallerMemberName] string caller = "", [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var wait = new ScaledWait(unscaled, caller, file, line);
        while (!condition())
        {
            if (wait.IsExpired) return false;
            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        wait.Succeeded();
        return true;
    }

    /// <summary>
    /// <see cref="UntilAsync(Func{bool}, TimeSpan, TimeSpan?, CancellationToken, string, string, int)"/> for a condition
    /// that is read asynchronously, such as an accessibility tree over D-Bus.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, TimeSpan unscaled, TimeSpan? interval = null,
        CancellationToken cancellationToken = default, [CallerMemberName] string caller = "", [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var wait = new ScaledWait(unscaled, caller, file, line);
        while (!await condition().ConfigureAwait(false))
        {
            if (wait.IsExpired) return false;
            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        wait.Succeeded();
        return true;
    }

    /// <summary>
    /// Reports a scaled wait that succeeded after <paramref name="elapsed"/>, more than its <paramref name="unscaled"/>
    /// limit: without the scale it would have failed.
    /// </summary>
    public static void ReportIfSlow(TimeSpan elapsed, TimeSpan unscaled, [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (Factor <= 1 || unscaled <= TimeSpan.Zero || unscaled == Timeout.InfiniteTimeSpan || elapsed <= unscaled) return;
        var path = SourcePath(file);
        if (!s_reported.TryAdd($"{path}:{line}", true)) return;
        var assembly = Assembly.GetEntryAssembly()?.GetName().Name;
        var message = string.Create(CultureInfo.InvariantCulture,
            $"{(assembly is null ? "" : assembly + ": ")}{caller} ({path}:{line}) waited {elapsed.TotalSeconds:0.###} s, past its unscaled {unscaled.TotalSeconds:0.###} s limit; it passed only because {Variable}={Factor}.");
        Write(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            ? $"::warning file={Property(path)},line={line},title={Property("Slow test wait")}::{Data(message)}"
            : $"warning: {message}");
    }

    // A deterministic CI build maps source paths to /_/; otherwise the path is made relative to the checkout.
    private static string SourcePath(string file)
    {
        var path = file.Replace('\\', '/');
        if (path.StartsWith("/_/", StringComparison.Ordinal)) return path[3..];
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")?.Replace('\\', '/').TrimEnd('/');
        return !string.IsNullOrEmpty(workspace) && path.StartsWith(workspace + "/", StringComparison.OrdinalIgnoreCase)
            ? path[(workspace.Length + 1)..] : path;
    }

    private static string Data(string value) => value.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
    private static string Property(string value) => Data(value).Replace(":", "%3A").Replace(",", "%2C");

    // Test frameworks capture Console per test; GitHub reads annotations only from the process's own output, at the
    // start of a line.
    private static void Write(string line)
    {
        var bytes = Encoding.UTF8.GetBytes($"{Environment.NewLine}{line}{Environment.NewLine}");
        lock (s_outputLock)
        {
            try
            {
                using var output = Console.OpenStandardOutput();
                output.Write(bytes);
                output.Flush();
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Times one condition wait: <see cref="Limit"/> is the scaled limit, and <see cref="Succeeded"/> reports a
    /// success after the unscaled one.
    /// </summary>
    public readonly struct ScaledWait
    {
        private readonly long _started;
        private readonly string _caller, _file;
        private readonly int _line;

        internal ScaledWait(TimeSpan unscaled, string caller, string file, int line)
        {
            _started = Stopwatch.GetTimestamp();
            Unscaled = unscaled;
            Limit = Scale(unscaled);
            (_caller, _file, _line) = (caller, file, line);
        }

        public TimeSpan Unscaled { get; }
        public TimeSpan Limit { get; }
        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);
        public TimeSpan Remaining => Limit == Timeout.InfiniteTimeSpan ? Limit : Limit - Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
        public bool IsExpired => Limit != Timeout.InfiniteTimeSpan && Elapsed >= Limit;

        /// <summary>Reports the wait as succeeded now.</summary>
        public void Succeeded() => ReportIfSlow(Elapsed, Unscaled, _caller, _file, _line);

        /// <summary>A <see cref="TimeoutException"/> that names the scaled and unscaled limits.</summary>
        public TimeoutException Expired(Exception? inner = null) => new(string.Create(CultureInfo.InvariantCulture,
            $"{_caller} ({SourcePath(_file)}:{_line}) timed out after {Limit.TotalSeconds:0.###} s ({Unscaled.TotalSeconds:0.###} s × {Variable}={Factor})."),
            inner);
    }
}
