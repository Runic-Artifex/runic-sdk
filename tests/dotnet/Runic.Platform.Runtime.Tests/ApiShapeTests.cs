using System.Reflection;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Runtime.Tests;

// The 0.9 API decisions for Runic.Platform and Runic.Platform.Runtime (W319-003 B2): one dispatcher and owner
// vocabulary (D26), uniform names (D27), diagnostics on every failed result (D28), failure codes whose zero is never a
// real failure (D29), limits read at run time (D36), the Runtime package's two audiences (D32) and the request-based
// picker contract (D46).
internal sealed class ApiShapeTests
{
    private static readonly Assembly Platform = typeof(PlatformResult<>).Assembly;
    private static readonly Assembly Runtime = typeof(INativeWindowOwner).Assembly;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool IsPublic(Assembly assembly, string name) => assembly.GetType(name)?.IsPublic == true;

    [Test]
    public void ZeroIsNeverARealFailureCode()
    {
        Check(default(PlatformFailureCode) == PlatformFailureCode.Unknown, "The default code must be Unknown.");
        string[] names = Enum.GetNames<PlatformFailureCode>();
        int[] values = [.. Enum.GetValues<PlatformFailureCode>().Select(value => (int)value)];
        Check(names.SequenceEqual(["Unknown", "PermissionDenied", "ResourceBusy", "InvalidData", "TooLarge", "IOError",
            "Conflict", "UserDismissed", "Locked", "NotFound", "Other"]), "The codes changed: " + string.Join(",", names));
        Check(values.SequenceEqual(Enumerable.Range(0, 11)), "The code numbering changed: " + string.Join(",", values));
    }

    [Test]
    public void EveryFailedResultCarriesADiagnostic()
    {
        var diagnostic = PlatformDiagnostic.FromErrno(5);
        Check(new PickerResult<IReadFileLease>.Failed(PlatformFailureCode.IOError, diagnostic).Diagnostic == diagnostic, "PickerResult.Failed");
        Check(new FileCommitResult.NotCommitted(PlatformFailureCode.IOError, diagnostic).Diagnostic == diagnostic, "NotCommitted");
        Check(new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError, diagnostic).Diagnostic == diagnostic, "CommitUnknown");
        // Positional, as on PlatformResult<T>.Failed: the diagnostic deconstructs with the code.
        var (code, deconstructed) = new FileCommitResult.CommitUnknown(PlatformFailureCode.Other);
        Check(code == PlatformFailureCode.Other && deconstructed is null, "The diagnostic defaults to null.");
        foreach (var type in new[] { typeof(PickerResult<IReadFileLease>.Failed), typeof(FileCommitResult.NotCommitted), typeof(FileCommitResult.CommitUnknown) })
        {
            var parameters = type.GetConstructors().Single(c => c.GetParameters().Length == 2).GetParameters();
            Check(parameters[1].Name == "Diagnostic" && parameters[1].HasDefaultValue, type.Name + " needs a positional Diagnostic = null.");
        }
    }

    [Test]
    public void FileErrorsKeepTheNativeCode()
    {
        if (OperatingSystem.IsWindows())
        {
            const int sharingViolation = unchecked((int)0x80070020);
            Check(FileErrors.Diagnose(new IOException("busy", sharingViolation)) is { Domain: "HRESULT", Code: 0x80070020 }, "An IOException keeps its HRESULT.");
        }
        else
        {
            Check(FileErrors.Diagnose(new IOException("io", 5)) is { Domain: "errno", Code: 5 }, "An IOException keeps its errno.");
            Check(FileErrors.Diagnose(new UnauthorizedAccessException()) is null, "A managed HRESULT is not an errno.");
        }
        Check(FileErrors.Diagnose(new IOException("no code")) is null || OperatingSystem.IsWindows(), "No code, no diagnostic.");
    }

    [Test]
    public void OneDispatcherAndOwnerVocabulary()
    {
        Check(Platform.GetType("Runic.Platform.IUiDispatcher") is null, "IUiDispatcher has no consumer and is removed.");
        Check(Runtime.GetType("Runic.Platform.Runtime.PresentationDispatcher") is null, "PresentationDispatcher is removed.");
        Check(Runtime.GetType("Runic.Platform.Runtime.INativePickerOwner") is null, "The owner is INativeWindowOwner.");
        Check(typeof(INativeWindowOwner).GetMethod(nameof(INativeWindowOwner.InvokeAsync)) is { } invoke
            && invoke.GetParameters()[0].ParameterType == typeof(Action<nint>), "INativeWindowOwner.InvokeAsync(Action<nint>, ...)");
    }

    [Test]
    public void NamesFollowTheFrameworkDesignGuidelines()
    {
        Check(Platform.GetType("Runic.Platform.DesktopStatusIcons") is null && typeof(DesktopStatusIcon).IsAbstract && typeof(DesktopStatusIcon).IsSealed,
            "The status icon factory is the static class DesktopStatusIcon.");
        Check(new DesktopApplicationInfo("a", "A") { IsHidden = true }.IsHidden, "IsHidden");
        var command = new DesktopMenuCommand("id", "Label") { IsChecked = true };
        Check(command.IsEnabled && command.IsChecked == true, "IsEnabled and IsChecked default and set.");
        Check(new DesktopMenuSubmenu("Label", []).IsEnabled, "DesktopMenuSubmenu.IsEnabled");
        Check(new DesktopOpenOptions(["org.example"]) { IncludeFiles = true }.IncludeFiles, "IncludeFiles");
        Check(new DesktopIcon { IconName = "folder" }.IconName == "folder", "DesktopIcon.IconName");
        foreach (var (type, old) in new[] { (typeof(DesktopApplicationInfo), "Hidden"), (typeof(DesktopMenuCommand), "Enabled"),
            (typeof(DesktopMenuCommand), "Checked"), (typeof(DesktopMenuSubmenu), "Enabled"), (typeof(DesktopOpenOptions), "Files"),
            (typeof(DesktopIcon), "ThemeName") })
            Check(type.GetProperty(old) is null, $"{type.Name}.{old} is renamed.");
    }

    [Test]
    public void LimitsThatMayChangeAreReadAtRunTime()
    {
        foreach (var (type, name) in new[] { (typeof(DesktopThumbnails), "MaximumSize"), (typeof(DesktopIconRequest), "MaximumSize"),
            (typeof(DesktopIconRequest), "MaximumScale") })
        {
            Check(type.GetField(name) is null, $"{type.Name}.{name} must not be a const compiled into callers.");
            Check(type.GetProperty(name, BindingFlags.Public | BindingFlags.Static) is not null, $"{type.Name}.{name} is a static property.");
        }
        Check(DesktopThumbnails.MaximumSize == 1024 && DesktopIconRequest.MaximumSize == 4096 && DesktopIconRequest.MaximumScale == 16, "Limits");
        // Fixed for the major version: they bound the privileged helper's protocol.
        Check(typeof(PrivilegedActionDeclaration).GetField(nameof(PrivilegedActionDeclaration.ArgumentLimit))?.IsLiteral == true, "ArgumentLimit stays const.");
    }

    [Test]
    public void RuntimeExposesOnlyTheApplicationApiAndTheProviderContract()
    {
        foreach (var name in new[] { "PresentationLifetime", "PresentationFiles", "PresentationClipboard", "PresentationFileLauncher",
            "DesktopServiceValidation", "InhibitionValidation", "SecretStoreValidation", "DesktopSettingsSource" })
            Check(!IsPublic(Runtime, "Runic.Platform.Runtime." + name), name + " is internal to Runic's providers and hosts.");
        foreach (var name in new[] { "ApplicationInstance", "ApplicationInstanceOptions", "LocalFileTransfers", "INativeWindowOwner",
            "IPickerBackend", "INativeFilePicker", "INativeDirectoryPicker", "NativePickerBackend", "NativeFileSelection",
            "NativeDirectorySelection", "NativeFileDialogRequest", "NativeFileLocation", "OwnerClosedException",
            "NativeBackendUnavailableException" })
            Check(IsPublic(Runtime, "Runic.Platform.Runtime." + name), name + " stays public.");
    }

    [Test]
    public void OpenRequestsTakeTheOptionsFirst()
    {
        var parameters = typeof(ApplicationInstance).GetMethod(nameof(ApplicationInstance.CreateOpenRequests))!.GetParameters();
        Check(parameters.Select(p => p.ParameterType).SequenceEqual([typeof(DesktopOpenOptions), typeof(IApplicationInstance), typeof(ApplicationLaunch)]),
            "CreateOpenRequests(options, instance = null, launch = null)");
        Check(parameters[1].HasDefaultValue && parameters[2].HasDefaultValue, "instance and launch are optional.");
    }

    [Test]
    public void ProviderExceptionsHaveTheStandardConstructors()
    {
        var inner = new IOException("native");
        Check(new OwnerClosedException("closed", inner).InnerException == inner && new OwnerClosedException().Message.Length > 0, "OwnerClosedException");
        Check(new NativeBackendUnavailableException("none", inner).InnerException == inner && new NativeBackendUnavailableException("none").Message == "none",
            "NativeBackendUnavailableException");
    }

    [Test]
    public void PickersImplementOnlyTheRequestMembers()
    {
        var file = typeof(INativeFilePicker).GetMethods().Where(m => m.Name == nameof(INativeFilePicker.SelectAsync)).ToArray();
        Check(file.Length == 1 && file[0].IsAbstract && file[0].GetParameters()[0].ParameterType == typeof(NativeFileDialogRequest),
            "INativeFilePicker.SelectAsync(NativeFileDialogRequest, CancellationToken) is the only, abstract, member.");
        var directory = typeof(INativeDirectoryPicker).GetMethods().Where(m => m.Name == nameof(INativeDirectoryPicker.SelectDirectoryAsync)).ToArray();
        Check(directory.Length == 1 && directory[0].IsAbstract && directory[0].GetParameters().Length == 2,
            "INativeDirectoryPicker.SelectDirectoryAsync(string?, CancellationToken) is the only, abstract, member.");
    }
}
