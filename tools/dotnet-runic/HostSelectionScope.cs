using System;
using System.Diagnostics;
using System.Threading;

namespace Runic.Application.Tool;

// Scoped to this asynchronous CLI invocation, never changes the user's process environment.
internal sealed class HostSelectionScope : IDisposable
{
    private static readonly AsyncLocal<string?> Current = new();
    private readonly string? _previous = Current.Value;

    internal HostSelectionScope(string? host)
    {
        if (!string.IsNullOrEmpty(host) && host is not ("desktop" or "cswebui"))
            throw new DevUsageException("RTKDEV1008", "Host must be 'desktop' or 'cswebui'.");
        if (!string.IsNullOrEmpty(host)) Current.Value = host;
    }

    internal static void Apply(ProcessStartInfo startInfo)
    {
        if (Current.Value is not { } host) return;
        startInfo.Environment["RunicHost"] = host;
        startInfo.Environment["VITE_RUNIC_HOST"] = host;
    }

    public void Dispose() => Current.Value = _previous;
}
