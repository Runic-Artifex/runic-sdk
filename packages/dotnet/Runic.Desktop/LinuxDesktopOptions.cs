using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Runic.Desktop;

/// <summary>The explicitly selected Linux embedded toolkit and WebKit ABI.</summary>
public enum LinuxEmbeddedBackend
{
    /// <summary>GTK 3 with WebKitGTK 4.1 (<c>libgtk-3.so.0</c> and <c>libwebkit2gtk-4.1.so.0</c>), hosted by Runic.Desktop itself.</summary>
    Gtk3WebKit41,

    /// <summary>GTK 4 with WebKitGTK 6.0 (<c>libgtk-4.so.1</c> and <c>libwebkitgtk-6.0.so.4</c>), hosted by the optional Runic.Desktop.Gtk4 provider.</summary>
    Gtk4WebKit6,
}

/// <summary>Linux presentation configuration; no toolkit is selected by default.</summary>
public sealed record LinuxDesktopOptions
{
    /// <summary>Gets the embedded toolkit to use on Linux, or <see langword="null"/> to select none.</summary>
    /// <remarks>
    /// Without a backend, Linux has no built-in embedded presentation; installed browsers remain available.
    /// <see cref="LinuxEmbeddedBackend.Gtk4WebKit6"/> also requires a matching <see cref="ILinuxDesktopWindowHostFactory"/>
    /// as the window host factory, and a configured Linux factory must report this backend.
    /// </remarks>
    public LinuxEmbeddedBackend? EmbeddedBackend { get; init; }
}

/// <summary>An optional Linux provider identifying its native toolkit.</summary>
public interface ILinuxDesktopWindowHostFactory : IDesktopWindowHostFactory
{
    /// <summary>Gets the toolkit and WebKit ABI that this factory's window hosts load.</summary>
    LinuxEmbeddedBackend Backend { get; }
}

/// <summary>Prevents incompatible Linux toolkits from initializing in one process.</summary>
public static class LinuxDesktopRuntime
{
    private static int _backend;
    private static readonly ConcurrentDictionary<string, bool> LibraryAvailability = new(StringComparer.Ordinal);
    private static readonly object LoaderCacheGate = new();
    // Set only by a complete ldconfig read; a read that failed or timed out is retried by a later probe.
    private static string[]? _loaderCache;

    // Lets tests decide which libraries exist; consulted before the process-lifetime cache.
    internal static Func<string, bool>? LibraryProbeOverride { get; set; }

    /// <summary>Inspects Linux loader paths without loading a toolkit into this process.</summary>
    /// <remarks>
    /// Checks the process search path and the system ldconfig cache, which is read once it has been read completely.
    /// Each definite result is cached for the process lifetime, so a library installed later is seen by a new process;
    /// a miss while the ldconfig cache could not be read is not cached. Native initialization remains the final
    /// runtime check.
    /// </remarks>
    public static bool IsLibraryAvailable(string libraryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        if (libraryName != Path.GetFileName(libraryName)) throw new ArgumentException("Use a library filename.", nameof(libraryName));
        if (!OperatingSystem.IsLinux()) return false;
        if (LibraryProbeOverride is { } probe) return probe(libraryName);
        if (LibraryAvailability.TryGetValue(libraryName, out var known)) return known;
        var (available, definite) = Probe(libraryName);
        if (definite) LibraryAvailability.TryAdd(libraryName, available);
        return available;
    }

    private static (bool Available, bool Definite) Probe(string libraryName)
    {
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        string[] standard = [AppContext.BaseDirectory, "/lib", "/usr/lib", "/lib64", "/usr/lib64", $"/lib/{architecture}-linux-gnu", $"/usr/lib/{architecture}-linux-gnu"];
        var paths = (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(standard);
        if (paths.Any(path => File.Exists(Path.Combine(path, libraryName)))) return (true, true);
        var cache = GetLoaderCache();
        if (cache is null) return (false, false);
        return (cache.Any(line =>
            line.TrimStart().StartsWith(libraryName + " ", StringComparison.Ordinal)
            && line.Split("=>", StringSplitOptions.TrimEntries) is [_, var path] && File.Exists(path)), true);
    }

    private static string[]? GetLoaderCache()
    {
        lock (LoaderCacheGate)
        {
            return _loaderCache ??= ReadLoaderCache();
        }
    }

    // Returns null when ldconfig could not be run or did not finish in time.
    private static string[]? ReadLoaderCache()
    {
        try
        {
            using var cache = Process.Start(new ProcessStartInfo("/sbin/ldconfig")
            {
                ArgumentList = { "-p" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (cache is null) return null;
            var output = cache.StandardOutput.ReadToEndAsync();
            var error = cache.StandardError.ReadToEndAsync();
            if (!cache.WaitForExit(1000)) { cache.Kill(); cache.WaitForExit(); return null; }
            _ = error.GetAwaiter().GetResult();
            return output.GetAwaiter().GetResult().Split('\n');
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    /// <summary>Claims the toolkit before native initialization. A process cannot switch toolkits.</summary>
    public static void ClaimBackend(LinuxEmbeddedBackend backend)
    {
        if (!Enum.IsDefined(backend)) throw new ArgumentOutOfRangeException(nameof(backend));
        int value = (int)backend + 1;
        int previous = Interlocked.CompareExchange(ref _backend, value, 0);
        if (previous != 0 && previous != value)
            throw new InvalidOperationException("A different Linux embedded toolkit has already initialized in this process. Start a new process to change backends.");
    }
    /// <summary>Gets whether the backend is compatible with the process toolkit claim, without loading it.</summary>
    public static bool CanUse(LinuxEmbeddedBackend backend) =>
        Volatile.Read(ref _backend) is var value && (value == 0 || value == (int)backend + 1);

    // Lets tests release a toolkit claim they made; a real process never switches toolkits.
    internal static void ReleaseBackendClaim() => Volatile.Write(ref _backend, 0);
}
