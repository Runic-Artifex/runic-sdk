using System;
using System.Text;

namespace WebUIToolkit.CommandLine.Processes;

/// <summary>Configures bounded process execution and output capture.</summary>
public sealed class ProcessExecutionOptions
{
    /// <summary>The default retained-byte cap for each output channel.</summary>
    public const int DefaultOutputLimitBytes = 1024 * 1024;

    /// <summary>The hard retained-byte ceiling for either output channel.</summary>
    public const int MaximumOutputLimitBytes = 16 * 1024 * 1024;

    /// <summary>The default grace period for exit and pipe drain after termination.</summary>
    public static readonly TimeSpan DefaultDrainGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>The hard ceiling for post-termination drain grace.</summary>
    public static readonly TimeSpan MaximumDrainGracePeriod = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan MaximumTimerDuration = TimeSpan.FromMilliseconds(4294967294);

    /// <summary>Initializes process execution options.</summary>
    /// <param name="timeout">Maximum execution time, or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="standardOutputLimitBytes">Maximum retained standard-output bytes.</param>
    /// <param name="standardErrorLimitBytes">Maximum retained standard-error bytes.</param>
    /// <param name="drainGracePeriod">Bounded wait after a termination request.</param>
    /// <param name="standardOutputEncoding">Standard-output encoding; UTF-8 without a BOM by default.</param>
    /// <param name="standardErrorEncoding">Standard-error encoding; UTF-8 without a BOM by default.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit or duration is outside its allowed range.</exception>
    public ProcessExecutionOptions(
        TimeSpan? timeout = null,
        int standardOutputLimitBytes = DefaultOutputLimitBytes,
        int standardErrorLimitBytes = DefaultOutputLimitBytes,
        TimeSpan? drainGracePeriod = null,
        Encoding? standardOutputEncoding = null,
        Encoding? standardErrorEncoding = null)
    {
        Timeout = timeout ?? System.Threading.Timeout.InfiniteTimeSpan;
        DrainGracePeriod = drainGracePeriod ?? DefaultDrainGracePeriod;

        if (Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Timeout, TimeSpan.Zero, nameof(timeout));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                Timeout,
                MaximumTimerDuration,
                nameof(timeout));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            DrainGracePeriod,
            TimeSpan.Zero,
            nameof(drainGracePeriod));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            DrainGracePeriod,
            MaximumDrainGracePeriod,
            nameof(drainGracePeriod));

        ArgumentOutOfRangeException.ThrowIfNegative(standardOutputLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(standardErrorLimitBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            standardOutputLimitBytes,
            MaximumOutputLimitBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            standardErrorLimitBytes,
            MaximumOutputLimitBytes);

        StandardOutputLimitBytes = standardOutputLimitBytes;
        StandardErrorLimitBytes = standardErrorLimitBytes;
        StandardOutputEncoding = standardOutputEncoding ?? new UTF8Encoding(false, false);
        StandardErrorEncoding = standardErrorEncoding ?? new UTF8Encoding(false, false);
    }

    /// <summary>Gets the maximum execution time.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets the standard-output retained-byte cap.</summary>
    public int StandardOutputLimitBytes { get; }

    /// <summary>Gets the standard-error retained-byte cap.</summary>
    public int StandardErrorLimitBytes { get; }

    /// <summary>Gets the bounded drain grace period after termination.</summary>
    public TimeSpan DrainGracePeriod { get; }

    /// <summary>Gets the standard-output encoding.</summary>
    public Encoding StandardOutputEncoding { get; }

    /// <summary>Gets the standard-error encoding.</summary>
    public Encoding StandardErrorEncoding { get; }
}
