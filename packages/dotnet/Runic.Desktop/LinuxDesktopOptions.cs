using System.Diagnostics;
using System.Runtime.InteropServices;

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
    /// <summary>Inspects Linux loader paths without loading a toolkit into this process.</summary>
    /// <remarks>Checks the process search path and the system ldconfig cache. Native initialization remains the final runtime check.</remarks>
    public static bool IsLibraryAvailable(string libraryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        if (libraryName != Path.GetFileName(libraryName)) throw new ArgumentException("Use a library filename.", nameof(libraryName));
        if (!OperatingSystem.IsLinux()) return false;
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        string[] standard = [AppContext.BaseDirectory, "/lib", "/usr/lib", "/lib64", "/usr/lib64", $"/lib/{architecture}-linux-gnu", $"/usr/lib/{architecture}-linux-gnu"];
        var paths = (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(standard);
        if (paths.Any(path => File.Exists(Path.Combine(path, libraryName)))) return true;
        try
        {
            using var cache = Process.Start(new ProcessStartInfo("/sbin/ldconfig")
            {
                ArgumentList = { "-p" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (cache is null) return false;
            var output = cache.StandardOutput.ReadToEndAsync();
            var error = cache.StandardError.ReadToEndAsync();
            if (!cache.WaitForExit(1000)) { cache.Kill(); cache.WaitForExit(); return false; }
            _ = error.GetAwaiter().GetResult();
            return output.GetAwaiter().GetResult().Split('\n').Any(line =>
                line.TrimStart().StartsWith(libraryName + " ", StringComparison.Ordinal)
                && line.Split("=>", StringSplitOptions.TrimEntries) is [_, var path] && File.Exists(path));
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return false; }
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
}
