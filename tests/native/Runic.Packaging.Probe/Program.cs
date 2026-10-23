using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;
using Runic.Platform;
using Runic.Platform.Windows;
using RunicWindowApp;

// Opened as runic-packaging-probe://receipt?path=<file>, as an installed package opens its URL scheme, the probe
// writes what the package gives it to <file> as JSON and exits: the URL, its identity on this platform, the content
// root Runic resolves, and on Windows what notifications, login startup and registration report there and which
// activation the package delivered. Opened with .runicprobe files instead, it writes the files it received to
// files-receipt.json beside the first. It also writes one diagnostic through the template's DiagnosticsLog, to
// standard error and the log.
var launch = OperatingSystem.IsWindows() ? WindowsPlatformProvider.GetLaunch(args) : ApplicationLaunch.FromProcess(args);
var opened = DesktopOpenRequests.FromLaunch(launch, new(["runic-packaging-probe"]) { IncludeFiles = true })?.Items ?? [];
string? url = opened.FirstOrDefault(static item => item.Scheme == "runic-packaging-probe")?.OriginalString;
string[] files = [.. opened.Where(static item => item.IsFile && item.LocalPath.EndsWith(".runicprobe", StringComparison.OrdinalIgnoreCase)).Select(static item => item.LocalPath)];
string? receipt = url is not null ? ReceiptPath(new Uri(url)) : files.Length > 0 ? Path.Combine(Path.GetDirectoryName(files[0])!, "files-receipt.json") : null;
DiagnosticsLog.Write("Info packaging-probe: the packaging probe ran.");
if (receipt is null)
{
    Console.Error.WriteLine("Open the probe as runic-packaging-probe://receipt?path=<receipt file>, or with .runicprobe files.");
    return 2;
}

var stream = new MemoryStream();
using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    json.WriteStartObject();
    json.WriteString("url", url);
    json.WriteString("applicationId", ApplicationIdentity.Id);
    json.WriteString("applicationName", ApplicationIdentity.Name);
    json.WriteString("packageIdentity", PackageIdentity());
    string contentRoot = new RunicWindowOptions().ResolveContentRoot();
    json.WriteString("contentRoot", contentRoot);
    json.WriteBoolean("contentRootHasIndex", File.Exists(Path.Combine(contentRoot, "index.html")));
    json.WriteString("diagnosticsLog", DiagnosticsLog.Path);
    json.WriteStartArray("files");
    foreach (string file in files) json.WriteStringValue(file);
    json.WriteEndArray();
    json.WriteNumber("processId", Environment.ProcessId);
    if (OperatingSystem.IsWindows())
    {
        await using var notifications = WindowsPlatformProvider.CreateNotifications(ApplicationIdentity.Id);
        json.WriteString("notifications", Describe(await notifications.RequestPermissionAsync()));
        var startup = WindowsPlatformProvider.CreateLoginStartup(ApplicationIdentity.Id);
        json.WriteString("loginStartup", Describe(await startup.IsEnabledAsync()));
        json.WriteString("openRegistration", Describe(await WindowsPlatformProvider.CreateOpenRegistration(ApplicationIdentity.Id)
            .IsRegisteredAsync(new DesktopOpenRegistration("Runic Packaging Probe") { UrlSchemes = ["runic-packaging-probe"] })));
        if (PackageIdentity() is not null)
        {
            json.WriteNumber("activationKind", OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134) ? WindowsActivation.Read()?.Kind ?? -1 : -1);
            // The package's startup task: on, then off again.
            if (url is not null)
                json.WriteString("loginStartupCycle", string.Join(",", Describe(await startup.EnableAsync(new([]))), Describe(await startup.IsEnabledAsync()),
                    Describe(await startup.DisableAsync()), Describe(await startup.IsEnabledAsync())));
        }
    }
    json.WriteEndObject();
}
File.WriteAllBytes(receipt, stream.ToArray());
return 0;

static string? ReceiptPath(Uri url)
{
    foreach (string pair in url.Query.TrimStart('?').Split('&'))
        if (pair.StartsWith("path=", StringComparison.Ordinal)) return Uri.UnescapeDataString(pair["path=".Length..]);
    return null;
}

static string Describe<T>(PlatformResult<T> result) => result switch
{
    PlatformResult<bool>.Success { Value: var value } => $"Success({value})",
    PlatformResult<T>.Success => "Success",
    PlatformResult<T>.Unavailable unavailable => $"Unavailable({unavailable.Reason})",
    PlatformResult<T>.Failed failed => $"Failed({failed.Code})",
    _ => result.GetType().Name,
};

// What the platform says the application is: the MSIX package's AppUserModelID, the bundle identifier that Launch
// Services gave the process, or the Flatpak application ID.
static string? PackageIdentity()
{
    if (OperatingSystem.IsWindows()) return Native.WindowsApplicationUserModelId();
    if (OperatingSystem.IsMacOS()) return Native.MacBundleIdentifier();
    return File.Exists("/.flatpak-info") ? Environment.GetEnvironmentVariable("FLATPAK_ID") : null;
}

internal static partial class Native
{
    internal static unsafe string? WindowsApplicationUserModelId()
    {
        uint length = 256;
        char* buffer = stackalloc char[(int)length];
        return GetCurrentApplicationUserModelId(ref length, buffer) == 0 ? new string(buffer, 0, (int)length - 1) : null;
    }

    internal static unsafe string? MacBundleIdentifier()
    {
        nint identifier = CFBundleGetIdentifier(CFBundleGetMainBundle());
        if (identifier == 0) return null;
        byte* buffer = stackalloc byte[512];
        return CFStringGetCString(identifier, buffer, 512, 0x08000100) != 0 ? Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(buffer)) : null;
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentApplicationUserModelId(ref uint length, char* applicationUserModelId);

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundation)]
    private static partial nint CFBundleGetMainBundle();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFBundleGetIdentifier(nint bundle);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial byte CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);
}
