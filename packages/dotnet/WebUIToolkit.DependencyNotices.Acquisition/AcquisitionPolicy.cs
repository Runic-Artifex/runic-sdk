using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace WebUIToolkit.DependencyNotices.Acquisition;

public sealed class AcquisitionPolicy
{
    public const long DefaultMaximumBytes = 16L * 1024L * 1024L;
    public const int DefaultMaximumRedirects = 5;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _allowedHosts;

    public AcquisitionPolicy(
        IEnumerable<string> allowedHosts,
        bool allowHttp = false,
        int maximumRedirects = DefaultMaximumRedirects,
        long maximumBytes = DefaultMaximumBytes,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRedirects);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRedirects, 20);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _allowedHosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (string host in allowedHosts)
        {
            _allowedHosts.Add(NormalizeHost(host));
        }

        if (_allowedHosts.Count == 0)
        {
            throw new ArgumentException("At least one explicitly allowed host is required.", nameof(allowedHosts));
        }

        AllowedHosts = new ReadOnlyCollection<string>([.. _allowedHosts.Order(StringComparer.Ordinal)]);
        AllowHttp = allowHttp;
        MaximumRedirects = maximumRedirects;
        MaximumBytes = maximumBytes;
        Timeout = effectiveTimeout;
    }

    public IReadOnlyList<string> AllowedHosts { get; }

    public bool AllowHttp { get; }

    public int MaximumRedirects { get; }

    public long MaximumBytes { get; }

    public TimeSpan Timeout { get; }

    internal bool IsHostAllowed(Uri origin) => _allowedHosts.Contains(origin.IdnHost.ToLowerInvariant());

    private static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        string trimmed = host.Trim();
        if (trimmed.Contains('*', StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.Contains(':', StringComparison.Ordinal) ||
            trimmed.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException("Allowed hosts must be exact DNS host names without ports or wildcards.", nameof(host));
        }

        Uri probe = new(string.Format(CultureInfo.InvariantCulture, "https://{0}/", trimmed), UriKind.Absolute);
        if (probe.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic)
        {
            throw new ArgumentException("Allowed hosts must be valid DNS or IP host names.", nameof(host));
        }

        return probe.IdnHost.ToLowerInvariant();
    }
}
