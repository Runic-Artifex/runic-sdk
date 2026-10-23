using System.Runtime.Versioning;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Runic.Platform.Windows;

// Per-user URL protocols and file types under HKCU\Software\Classes, as open-activation.md lists them: a protocol key
// and a ProgID <applicationId>.<extension> whose command is "<exe>" "%1", the extension's OpenWithProgids entry, and
// Capabilities registered under RegisteredApplications so Settings > Default apps lists the application. The
// extension's default is never set: the user chooses defaults. Every key Runic writes carries the application ID, so
// an earlier registration of the same application updates; a key without it, or one in HKLM, that names another
// command belongs to another application and is a conflict, checked for every item before anything is written.
[SupportedOSPlatform("windows")]
internal sealed class WindowsOpenRegistration : IDesktopOpenRegistration
{
    internal const string Marker = "RunicApplicationId";
    private readonly string _applicationId;
    private readonly RegistryKey _user;
    private readonly string _classes, _software, _registeredApplications;
    private readonly Func<RegistryKey?> _machineClasses;

    internal WindowsOpenRegistration(string applicationId) : this(applicationId, Registry.CurrentUser, @"Software\Classes", "Software",
        @"Software\RegisteredApplications", static () => Registry.LocalMachine.OpenSubKey(@"Software\Classes"))
    { }

    // The keys under the given user root, so tests run in a scratch key.
    internal WindowsOpenRegistration(string applicationId, RegistryKey user, string classes, string software, string registeredApplications,
        Func<RegistryKey?> machineClasses)
    {
        _applicationId = applicationId; _user = user; _classes = classes; _software = software;
        _registeredApplications = registeredApplications; _machineClasses = machineClasses;
    }

    private string CapabilitiesPath => _software + @"\" + _applicationId + @"\Capabilities";
    internal string ProgId(DesktopFileType type) => _applicationId + type.Extension.ToLowerInvariant();

    public ValueTask<PlatformResult<bool>> IsRegisteredAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        return Run(() =>
        {
            string command = Command(registration);
            using var classes = _user.OpenSubKey(_classes);
            foreach (string scheme in registration.UrlSchemes)
                if (!Points(classes, scheme, command)) return new PlatformResult<bool>.Success(false);
            foreach (var type in registration.FileTypes)
            {
                using var openWith = classes?.OpenSubKey(type.Extension + @"\OpenWithProgids");
                if (openWith is null || !openWith.GetValueNames().Contains(ProgId(type), StringComparer.OrdinalIgnoreCase) || !Points(classes, ProgId(type), command))
                    return new PlatformResult<bool>.Success(false);
            }
            return new PlatformResult<bool>.Success(true);
        });
    }

    public ValueTask<PlatformResult<PlatformUnit>> RegisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.UrlSchemes.IsEmpty && registration.FileTypes.IsEmpty)
            throw new ArgumentException("Supply at least one URL scheme or file type.", nameof(registration));
        cancellationToken.ThrowIfCancellationRequested();
        return Run<PlatformUnit>(() =>
        {
            string command = Command(registration);
            if (Conflict(registration, command) is { } conflict)
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Conflict, conflict);
            string executable = Executable(registration);
            using var classes = _user.CreateSubKey(_classes, writable: true);
            using var capabilities = _user.CreateSubKey(CapabilitiesPath, writable: true);
            capabilities.SetValue(Marker, _applicationId);
            capabilities.SetValue("ApplicationName", registration.DisplayName);
            capabilities.SetValue("ApplicationDescription", registration.DisplayName);
            foreach (string scheme in registration.UrlSchemes)
            {
                using var key = classes.CreateSubKey(scheme, writable: true);
                key.SetValue("", "URL:" + registration.DisplayName);
                key.SetValue("URL Protocol", "");
                key.SetValue(Marker, _applicationId);
                using (var icon = key.CreateSubKey("DefaultIcon", writable: true)) icon.SetValue("", Quote(executable) + ",0");
                using (var open = key.CreateSubKey(@"shell\open\command", writable: true)) open.SetValue("", command);
                using var urls = capabilities.CreateSubKey("URLAssociations", writable: true);
                urls.SetValue(scheme, scheme);
            }
            foreach (var type in registration.FileTypes)
            {
                string progId = ProgId(type);
                using (var key = classes.CreateSubKey(progId, writable: true))
                {
                    key.SetValue("", type.Description ?? registration.DisplayName + " (" + type.Extension + ")");
                    key.SetValue(Marker, _applicationId);
                    using (var icon = key.CreateSubKey("DefaultIcon", writable: true)) icon.SetValue("", type.IconPath ?? Quote(executable) + ",0");
                    using var open = key.CreateSubKey(@"shell\open\command", writable: true);
                    open.SetValue("", command);
                }
                using (var openWith = classes.CreateSubKey(type.Extension + @"\OpenWithProgids", writable: true))
                    openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
                using var files = capabilities.CreateSubKey("FileAssociations", writable: true);
                files.SetValue(type.Extension, progId);
            }
            using (var registered = _user.CreateSubKey(_registeredApplications, writable: true))
                registered.SetValue(_applicationId, CapabilitiesPath);
            Notify();
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        });
    }

    public ValueTask<PlatformResult<bool>> UnregisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        return Run(() =>
        {
            bool removed = false;
            using var classes = _user.OpenSubKey(_classes, writable: true);
            using var capabilities = _user.OpenSubKey(CapabilitiesPath, writable: true);
            foreach (string scheme in registration.UrlSchemes)
            {
                if (Owned(classes, scheme)) { classes!.DeleteSubKeyTree(scheme, throwOnMissingSubKey: false); removed = true; }
                using var urls = capabilities?.OpenSubKey("URLAssociations", writable: true);
                urls?.DeleteValue(scheme, throwOnMissingValue: false);
            }
            foreach (var type in registration.FileTypes)
            {
                string progId = ProgId(type);
                if (Owned(classes, progId)) { classes!.DeleteSubKeyTree(progId, throwOnMissingSubKey: false); removed = true; }
                using (var openWith = classes?.OpenSubKey(type.Extension + @"\OpenWithProgids", writable: true))
                {
                    if (openWith?.GetValue(progId) is not null) { openWith.DeleteValue(progId); removed = true; }
                    if (openWith is { ValueCount: 0, SubKeyCount: 0 }) classes!.DeleteSubKey(type.Extension + @"\OpenWithProgids", throwOnMissingSubKey: false);
                }
                using (var extension = classes?.OpenSubKey(type.Extension))
                    if (extension is { ValueCount: 0, SubKeyCount: 0 }) classes!.DeleteSubKey(type.Extension, throwOnMissingSubKey: false);
                using var files = capabilities?.OpenSubKey("FileAssociations", writable: true);
                files?.DeleteValue(type.Extension, throwOnMissingValue: false);
            }
            // The Capabilities go with the last association.
            if (capabilities is not null && Associations(capabilities) == 0 && Equals(capabilities.GetValue(Marker), _applicationId))
            {
                _user.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);
                using (var application = _user.OpenSubKey(_software + @"\" + _applicationId))
                    if (application is { ValueCount: 0, SubKeyCount: 0 }) _user.DeleteSubKey(_software + @"\" + _applicationId, throwOnMissingSubKey: false);
                using var registered = _user.OpenSubKey(_registeredApplications, writable: true);
                if (registered?.GetValue(_applicationId) is string path && path.Equals(CapabilitiesPath, StringComparison.OrdinalIgnoreCase))
                    registered.DeleteValue(_applicationId);
            }
            if (removed) Notify();
            return new PlatformResult<bool>.Success(removed);
        });
    }

    // The first item that another application registered, checked before anything is written.
    private PlatformDiagnostic? Conflict(DesktopOpenRegistration registration, string command)
    {
        using var classes = _user.OpenSubKey(_classes);
        using var machine = _machineClasses();
        var names = registration.UrlSchemes.Concat(registration.FileTypes.Select(ProgId));
        foreach (string name in names)
        {
            if (Foreign(classes, name, command)) return Diagnostic(@"HKEY_CURRENT_USER\" + _classes + @"\" + name);
            if (Foreign(machine, name, command, machine: true)) return Diagnostic(@"HKEY_LOCAL_MACHINE\Software\Classes\" + name);
        }
        using (var capabilities = _user.OpenSubKey(CapabilitiesPath))
            if (capabilities is { ValueCount: > 0 } && !Equals(capabilities.GetValue(Marker), _applicationId))
                return Diagnostic(@"HKEY_CURRENT_USER\" + CapabilitiesPath);
        using (var registered = _user.OpenSubKey(_registeredApplications))
            if (registered?.GetValue(_applicationId) is { } path && !(path is string text && text.Equals(CapabilitiesPath, StringComparison.OrdinalIgnoreCase)))
                return Diagnostic(@"HKEY_CURRENT_USER\" + _registeredApplications + @"\" + _applicationId);
        return null;
    }

    private static PlatformDiagnostic Diagnostic(string key) =>
        new("Runic.OpenRegistration", 0, $"{key} belongs to another application; nothing was registered.");

    // A key that exists, without this application's marker, whose command is not this registration's.
    private bool Foreign(RegistryKey? classes, string name, string command, bool machine = false)
    {
        using var key = classes?.OpenSubKey(name);
        if (key is null) return false;
        if (!machine && Equals(key.GetValue(Marker), _applicationId)) return false;
        using var open = key.OpenSubKey(@"shell\open\command");
        string? existing = open?.GetValue("") as string;
        if (existing is null) return machine ? key.GetValue("URL Protocol") is not null || key.SubKeyCount > 0 : key.ValueCount > 0 || key.SubKeyCount > 0;
        return !existing.Equals(command, StringComparison.OrdinalIgnoreCase);
    }

    private bool Owned(RegistryKey? classes, string name)
    {
        using var key = classes?.OpenSubKey(name);
        return key is not null && Equals(key.GetValue(Marker), _applicationId);
    }

    private bool Points(RegistryKey? classes, string name, string command)
    {
        using var key = classes?.OpenSubKey(name);
        using var open = key?.OpenSubKey(@"shell\open\command");
        return key is not null && Equals(key.GetValue(Marker), _applicationId)
            && open?.GetValue("") is string existing && existing.Equals(command, StringComparison.OrdinalIgnoreCase);
    }

    private static int Associations(RegistryKey capabilities)
    {
        using var urls = capabilities.OpenSubKey("URLAssociations");
        using var files = capabilities.OpenSubKey("FileAssociations");
        return (urls?.ValueCount ?? 0) + (files?.ValueCount ?? 0);
    }

    private static string Executable(DesktopOpenRegistration registration) =>
        registration.Executable ?? Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown; set Executable.");

    // "<exe>" "%1", or "dotnet" "<app.dll>" "%1" for a framework-dependent application started by dotnet.
    internal static string Command(DesktopOpenRegistration registration)
    {
        string executable = Executable(registration);
        var parts = new List<string> { Quote(executable) };
        if (registration.Executable is null && Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && Environment.GetCommandLineArgs() is [{ } assembly, ..] && assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            parts.Add(Quote(Path.GetFullPath(assembly)));
        parts.Add("\"%1\"");
        return string.Join(' ', parts);
    }

    private static string Quote(string path) => "\"" + path + "\"";

    private static unsafe void Notify()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600)) PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);
    }

    private static ValueTask<PlatformResult<T>> Run<T>(Func<PlatformResult<T>> action)
    {
        try { return ValueTask.FromResult(action()); }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromHResult(error.HResult)));
        }
        catch (IOException error)
        {
            return ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Failed(PlatformFailureCode.IOError, PlatformDiagnostic.FromHResult(error.HResult)));
        }
    }
}
