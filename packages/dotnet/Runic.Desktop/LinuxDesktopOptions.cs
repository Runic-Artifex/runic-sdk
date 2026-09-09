namespace Runic.Desktop;

/// <summary>The explicitly selected Linux embedded toolkit and WebKit ABI.</summary>
public enum LinuxEmbeddedBackend { Gtk3WebKit41, Gtk4WebKit6 }

/// <summary>Linux presentation configuration; no toolkit is selected by default.</summary>
public sealed record LinuxDesktopOptions
{
    public LinuxEmbeddedBackend? EmbeddedBackend { get; init; }
}

/// <summary>An optional Linux provider identifying its native toolkit.</summary>
public interface ILinuxDesktopWindowHostFactory : IDesktopWindowHostFactory
{
    LinuxEmbeddedBackend Backend { get; }
}

/// <summary>Prevents incompatible Linux toolkits from initializing in one process.</summary>
public static class LinuxDesktopRuntime
{
    private static int _backend;
    /// <summary>Claims the toolkit before native initialization. A process cannot switch toolkits.</summary>
    public static void ClaimBackend(LinuxEmbeddedBackend backend)
    {
        if (!Enum.IsDefined(backend)) throw new ArgumentOutOfRangeException(nameof(backend));
        int value = (int)backend + 1;
        int previous = Interlocked.CompareExchange(ref _backend, value, 0);
        if (previous != 0 && previous != value)
            throw new InvalidOperationException("A different Linux embedded toolkit has already initialized in this process. Start a new process to change backends.");
    }
    internal static bool CanUse(LinuxEmbeddedBackend backend) =>
        Volatile.Read(ref _backend) is var value && (value == 0 || value == (int)backend + 1);
}
