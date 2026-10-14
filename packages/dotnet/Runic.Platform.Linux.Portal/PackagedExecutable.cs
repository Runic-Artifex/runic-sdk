using System.Runtime.CompilerServices;

namespace Runic.Platform.Linux.Portal;

// The wrapper a package starts the program through (the Nix builder's sets the libraries' paths), which names itself
// in RUNIC_APPLICATION_EXECUTABLE. It is read once, when this assembly loads, and removed from the environment, so
// the processes the application starts later do not take it for theirs. Native code that spawns processes, such as
// WebKitGTK's, reads the C environment, which .NET does not change; those processes are not Runic applications.
internal static class PackagedExecutable
{
    internal const string Variable = "RUNIC_APPLICATION_EXECUTABLE";

    internal static string? Wrapper { get; set; }

#pragma warning disable CA2255 // A library's module initializer: read the variable before the application starts any process.
    [ModuleInitializer]
    internal static void Capture()
    {
        Wrapper = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, null);
    }
#pragma warning restore CA2255

    // The wrapper when it belongs to the process's package: it is <prefix>/bin/<name>, and the process is installed
    // below <prefix>, as /nix/store/<hash>-app/bin/App and /nix/store/<hash>-app/lib/app/App are. Otherwise the
    // process itself, so a value inherited from another application is ignored.
    internal static string? Resolve(string? wrapper, string? process)
    {
        if (string.IsNullOrEmpty(wrapper) || process is null || !Path.IsPathFullyQualified(wrapper) || !File.Exists(wrapper))
            return process;
        string full = Path.GetFullPath(wrapper);
        if (Path.GetDirectoryName(full) is not { } bin || Path.GetFileName(bin) != "bin" || Path.GetDirectoryName(bin) is not { Length: > 1 } prefix)
            return process;
        return Path.GetFullPath(process).StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : process;
    }
}
