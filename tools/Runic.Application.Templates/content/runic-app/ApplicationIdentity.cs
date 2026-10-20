using System.Reflection;

namespace RunicWindowApp;

// The identity that the project file declares (ApplicationId and ApplicationTitle), read from the assembly so
// the ID is written in one place. Packages register the same ID: the desktop entry and Flatpak on Linux, the bundle on
// macOS and the MSIX package or the installer's shortcut on Windows.
public static class ApplicationIdentity
{
    public static string Id { get; } = Read("ApplicationId");

    public static string Name { get; } = Read("ApplicationTitle");

    private static string Read(string key) =>
        typeof(ApplicationIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value
        ?? throw new InvalidOperationException($"The project file sets no {key}.");
}
#if (desktopHost)

// Desktop diagnostics, such as a missing WebView runtime, go to standard error and to diagnostics.log under the user's
// local application data, in a folder named after the application ID: a Windows application (WinExe) and an
// application started from a launcher, Finder or Explorer have no visible standard error. The log starts empty each
// time the application starts and keeps at most 256 KiB. It holds the diagnostics only, never arguments or data.
public static class DiagnosticsLog
{
    private const long Limit = 256 * 1024;
    private static readonly Lock Gate = new();
    private static bool _started;

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        ApplicationIdentity.Id, "diagnostics.log");

    public static void Write(string line)
    {
        Console.Error.WriteLine(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                using var file = new FileStream(Path, _started ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                _started = true;
                if (file.Length < Limit)
                {
                    using var writer = new StreamWriter(file);
                    writer.WriteLine($"{DateTimeOffset.Now:O} {line}");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Standard error still has the diagnostic.
            }
        }
    }
}
#endif
