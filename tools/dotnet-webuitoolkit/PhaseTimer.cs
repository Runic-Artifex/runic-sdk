using System;
using System.Diagnostics;
using System.Globalization;

namespace WebUIToolkit.DotNet.WebUIToolkit;

/// <summary>Reports one bounded development-command phase without hiding child output.</summary>
internal sealed class PhaseTimer : IDisposable
{
    private readonly string _name;
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _completed;

    private PhaseTimer(string name)
    {
        _name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A phase name is required.", nameof(name))
            : name;
        Console.WriteLine($"[dev] {_name}...");
    }

    internal static PhaseTimer Start(string name) => new(name);

    internal void Complete()
    {
        if (System.Threading.Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        Console.WriteLine($"[dev] {_name} completed in {Format(Stopwatch.GetElapsedTime(_started))}.");
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _completed, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[dev] {_name} failed after {Format(Stopwatch.GetElapsedTime(_started))}.");
        }
    }

    internal static string Format(TimeSpan elapsed) =>
        elapsed.TotalSeconds >= 1
            ? elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s"
            : Math.Max(1, (int)Math.Round(elapsed.TotalMilliseconds))
                .ToString(CultureInfo.InvariantCulture) + " ms";
}
