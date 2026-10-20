using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Versioning;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Runtime.Tests;

// The 0.9 API decisions for Runic.Platform and Runic.Platform.Runtime (W319-003 B2): one dispatcher and owner
// vocabulary (D26), uniform names (D27), diagnostics on every failed result (D28), failure codes whose zero is never a
// real failure (D29), limits read at run time (D36), the Runtime package's two audiences (D32) and the request-based
// picker contract (D46).
internal sealed class ApiShapeTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // The cases run in the NativeAOT test executable too, so reflection goes through trim-annotated helpers.
    private static ParameterInfo[] ConstructorParameters([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type, int count) =>
        type.GetConstructors().Single(c => c.GetParameters().Length == count).GetParameters();

    private static bool HasProperty([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type, string name) =>
        type.GetProperty(name) is not null;

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
        foreach (var parameters in new[] { ConstructorParameters(typeof(PickerResult<IReadFileLease>.Failed), 2),
            ConstructorParameters(typeof(FileCommitResult.NotCommitted), 2), ConstructorParameters(typeof(FileCommitResult.CommitUnknown), 2) })
            Check(parameters[1].Name == "Diagnostic" && parameters[1].HasDefaultValue, "A failed result needs a positional Diagnostic = null.");
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
        // IUiDispatcher, PresentationDispatcher and INativePickerOwner are gone: PublicAPI.Unshipped.txt records it, and
        // this file compiles only against INativeWindowOwner.
        Check(typeof(INativeWindowOwner).GetMethod(nameof(INativeWindowOwner.InvokeAsync)) is { } invoke
            && invoke.GetParameters()[0].ParameterType == typeof(Action<nint>), "INativeWindowOwner.InvokeAsync(Action<nint>, ...)");
    }

    [Test]
    public void NamesFollowTheFrameworkDesignGuidelines()
    {
        Check(typeof(DesktopStatusIcon).IsAbstract && typeof(DesktopStatusIcon).IsSealed && DesktopStatusIcon.Unavailable(PlatformUnavailableReason.BackendUnavailable) is not null,
            "The status icon factory is the static class DesktopStatusIcon.");
        Check(new DesktopApplicationInfo("a", "A") { IsHidden = true }.IsHidden, "IsHidden");
        var command = new DesktopMenuCommand("id", "Label") { IsChecked = true };
        Check(command.IsEnabled && command.IsChecked == true, "IsEnabled and IsChecked default and set.");
        Check(new DesktopMenuSubmenu("Label", []).IsEnabled, "DesktopMenuSubmenu.IsEnabled");
        Check(new DesktopOpenOptions(["org.example"]) { IncludeFiles = true }.IncludeFiles, "IncludeFiles");
        Check(new DesktopIcon { IconName = "folder" }.IconName == "folder", "DesktopIcon.IconName");
        Check(!HasProperty(typeof(DesktopApplicationInfo), "Hidden") && !HasProperty(typeof(DesktopMenuCommand), "Enabled")
            && !HasProperty(typeof(DesktopMenuCommand), "Checked") && !HasProperty(typeof(DesktopMenuSubmenu), "Enabled")
            && !HasProperty(typeof(DesktopOpenOptions), "Files") && !HasProperty(typeof(DesktopIcon), "ThemeName"), "The old names are gone.");
    }

    [Test]
    public void LimitsThatMayChangeAreReadAtRunTime()
    {
        Check(typeof(DesktopThumbnails).GetField("MaximumSize") is null && typeof(DesktopThumbnails).GetProperty("MaximumSize") is not null,
            "DesktopThumbnails.MaximumSize is a static property, not a const compiled into callers.");
        Check(typeof(DesktopIconRequest).GetField("MaximumSize") is null && typeof(DesktopIconRequest).GetProperty("MaximumSize") is not null
            && typeof(DesktopIconRequest).GetField("MaximumScale") is null && typeof(DesktopIconRequest).GetProperty("MaximumScale") is not null,
            "DesktopIconRequest's limits are static properties.");
        Check(DesktopThumbnails.MaximumSize == 1024 && DesktopIconRequest.MaximumSize == 4096 && DesktopIconRequest.MaximumScale == 16, "Limits");
        // Fixed for the major version: they bound the privileged helper's protocol.
        Check(typeof(PrivilegedActionDeclaration).GetField(nameof(PrivilegedActionDeclaration.ArgumentLimit))?.IsLiteral == true, "ArgumentLimit stays const.");
    }

    [Test]
    public void RuntimeExposesOnlyTheApplicationApiAndTheProviderContract()
    {
        foreach (var type in new[] { typeof(PresentationLifetime), typeof(PresentationFiles), typeof(PresentationClipboard), typeof(PresentationFileLauncher),
            typeof(DesktopServiceValidation), typeof(InhibitionValidation), typeof(SecretStoreValidation), typeof(DesktopSettingsSource) })
            Check(!type.IsPublic, type.Name + " is internal to Runic's providers and hosts.");
        foreach (var type in new[] { typeof(ApplicationInstance), typeof(ApplicationInstanceOptions), typeof(LocalFileTransfers), typeof(INativeWindowOwner),
            typeof(IPickerBackend), typeof(INativeFilePicker), typeof(INativeDirectoryPicker), typeof(NativePickerBackend), typeof(NativeFileSelection),
            typeof(NativeDirectorySelection), typeof(NativeFileDialogRequest), typeof(NativeFileLocation), typeof(OwnerClosedException),
            typeof(NativeBackendUnavailableException) })
            Check(type.IsPublic, type.Name + " stays public.");
    }

    // D33/D34 (W319-003 B2): every provider offers the same application-scoped services, the window services are
    // internal to Runic's hosts, and the providers that throw off their platform say so for CA1416.
    [Test]
    public void ProvidersAreUniformAndAnnotated()
    {
        CheckProvider(typeof(Windows.WindowsPlatformProvider));
        CheckProvider(typeof(MacOS.MacOSPlatformProvider));
        CheckProvider(typeof(Linux.Portal.PortalApplication));
        Check(typeof(Windows.WindowsPlatformProvider).GetCustomAttribute<SupportedOSPlatformAttribute>()?.PlatformName == "windows", "WindowsPlatformProvider is annotated");
        Check(typeof(MacOS.MacOSPlatformProvider).GetCustomAttribute<SupportedOSPlatformAttribute>()?.PlatformName == "macos", "MacOSPlatformProvider is annotated");
        Check(typeof(Linux.Portal.PortalApplication).Assembly.GetCustomAttribute<SupportedOSPlatformAttribute>()?.PlatformName == "linux", "Runic.Platform.Linux.Portal is annotated");
        Check(typeof(Linux.Portal.PortalApplication).GetMethod("CreateUnparentedInhibition") is null && typeof(Linux.Portal.PortalApplication).GetMethod("OpenUriAsync") is null,
            "Portal's duplicates are gone.");
        // Linux's single entry point is PortalApplication; the GTK 3 package has no public types.
        foreach (var type in new[] { typeof(Linux.Portal.PortalPlatformProvider), typeof(Linux.LinuxPlatformProvider), typeof(Linux.LinuxTextClipboard),
            typeof(Linux.Gtk3PortalWindowOwner), typeof(IPortalWindowOwner), typeof(PortalParentLease) })
            Check(!type.IsPublic, type.Name + " is internal.");
        if (!OperatingSystem.IsLinux())
        {
#pragma warning disable CA1416 // The case checks the wrong-OS behaviour that the annotation documents.
            try { _ = new Linux.Portal.PortalApplication(); throw new InvalidOperationException("PortalApplication must throw off Linux."); }
            catch (PlatformNotSupportedException) { }
#pragma warning restore CA1416
        }
    }

    // Every provider offers the same application-scoped services, and no window service: Runic's hosts create those.
    private static void CheckProvider([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type provider)
    {
        var methods = provider.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var name in new[] { "CreateSettings", "CreateNotifications", "CreateLoginStartup", "CreateStatusIcon", "CreateInhibition",
            "CreateSecretStore", "CreateTrash", "CreateApplications", "CreateThumbnails", "CreateVolumes" })
            Check(methods.Any(m => m.Name == name), $"{provider.Name}.{name}");
        Check(!methods.Any(m => m.GetParameters().Any(p => typeof(INativeWindowOwner).IsAssignableFrom(p.ParameterType))),
            $"{provider.Name} has no public window service.");
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
