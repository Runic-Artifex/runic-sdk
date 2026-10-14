using System.Runtime.InteropServices;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Login startup through SMAppService.mainAppService (ServiceManagement, macOS 13 and later), which registers the
// application bundle as a login item. System Settings, General, Login Items lists it, and the user can turn it off
// there. A process outside an application bundle cannot be a login item.
internal sealed class MacLoginStartup : IDesktopLoginStartup
{
    private static readonly Lazy<bool> ServiceManagement = new(static () =>
        NativeLibrary.TryLoad("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement", out _)
        && Class("SMAppService") != 0);

    /// <summary>Whether ServiceManagement offers SMAppService, which needs macOS 13.</summary>
    internal static bool IsServiceAvailable => OperatingSystem.IsMacOS() && ServiceManagement.Value;

    /// <summary>Whether this process runs from an application bundle with a bundle identifier.</summary>
    internal static bool IsBundled
    {
        get
        {
            // ServiceManagement links Foundation, which NSBundle needs.
            if (!IsServiceAvailable) return false;
            using var pool = new Pool();
            var bundle = Send(Class("NSBundle"), Sel("mainBundle"));
            return MacLoginStartupModel.IsApplicationBundle(Text(Send(bundle, Sel("bundleIdentifier"))), Text(Send(bundle, Sel("bundlePath"))));
        }
    }

    public ValueTask<PlatformResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Service() is not { } service) return Result<bool>(new PlatformResult<bool>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        using var pool = new Pool();
        return Result(MacLoginStartupModel.Enabled(Status(service)));
    }

    public ValueTask<PlatformResult<PlatformUnit>> EnableAsync(LoginStartupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Arguments.IsDefault || request.Arguments.Length > 64 || request.Arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("Supply at most 64 arguments without NUL characters.", nameof(request));
        if (request.Reason is { Length: > 256 }) throw new ArgumentException("The reason is at most 256 characters.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Service() is not { } service) return Result<PlatformUnit>(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        using var pool = new Pool();
        // A registered item that the user turned off waits for their approval: only the user turns it on.
        long status = Status(service);
        if (status is MacLoginStartupModel.StatusEnabled or MacLoginStartupModel.StatusRequiresApproval)
            return Result(MacLoginStartupModel.Registered(status, null));
        PlatformDiagnostic? failure = BoolError(service, Sel("registerAndReturnError:"), out var error) == 0 && error != 0 ? ErrorDiagnostic(error) : null;
        return Result(MacLoginStartupModel.Registered(Status(service), failure));
    }

    public ValueTask<PlatformResult<PlatformUnit>> DisableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Service() is not { } service) return Result<PlatformUnit>(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        using var pool = new Pool();
        if (MacLoginStartupModel.IsUnregistered(Status(service))) return Result(MacLoginStartupModel.Unregistered(Status(service), null));
        PlatformDiagnostic? failure = BoolError(service, Sel("unregisterAndReturnError:"), out var error) == 0 && error != 0 ? ErrorDiagnostic(error) : null;
        return Result(MacLoginStartupModel.Unregistered(Status(service), failure));
    }

    // SMAppService.mainAppService for a bundled process on macOS 13 or later.
    private static nint? Service()
    {
        if (!IsBundled) return null;
        return Send(Class("SMAppService"), Sel("mainAppService")) is var service and not 0 ? service : null;
    }

    private static long Status(nint service) => Send(service, Sel("status"));

    private static ValueTask<PlatformResult<T>> Result<T>(PlatformResult<T> result) => ValueTask.FromResult(result);
}

// SMAppService outcomes, without AppKit. The status after a call decides, so error codes, whose domains differ
// between macOS versions, only classify a call that changed nothing.
internal static class MacLoginStartupModel
{
    // SMAppServiceStatus.
    internal const long StatusNotRegistered = 0, StatusEnabled = 1, StatusRequiresApproval = 2, StatusNotFound = 3;
    // EPERM, which SMAppService reports for a denied or unsigned registration.
    private const long OperationNotPermitted = 1;

    /// <summary>The hint attached to a registration that waits for the user.</summary>
    internal static readonly PlatformDiagnostic ApprovalHint = new("SMAppServiceStatus", StatusRequiresApproval,
        "The login item needs the user's approval in System Settings, General, Login Items.");

    internal static bool IsApplicationBundle(string identifier, string bundlePath) =>
        identifier.Length > 0 && bundlePath.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnregistered(long status) => status is StatusNotRegistered or StatusNotFound;

    /// <summary>Whether the application starts at login. One that waits for approval or that the user turned off does not.</summary>
    internal static PlatformResult<bool> Enabled(long status) => new PlatformResult<bool>.Success(status == StatusEnabled);

    /// <summary>The outcome of registering: enabled, or denied until the user approves it in System Settings.</summary>
    internal static PlatformResult<PlatformUnit> Registered(long status, PlatformDiagnostic? error) => status switch
    {
        StatusEnabled => new PlatformResult<PlatformUnit>.Success(new PlatformUnit()),
        StatusRequiresApproval => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, ApprovalHint),
        _ => Failed(error ?? new PlatformDiagnostic("SMAppServiceStatus", status)),
    };

    /// <summary>The outcome of unregistering: success once the item is gone.</summary>
    internal static PlatformResult<PlatformUnit> Unregistered(long status, PlatformDiagnostic? error) =>
        IsUnregistered(status) ? new PlatformResult<PlatformUnit>.Success(new PlatformUnit())
        : Failed(error ?? new PlatformDiagnostic("SMAppServiceStatus", status));

    private static PlatformResult<PlatformUnit> Failed(PlatformDiagnostic error) =>
        new PlatformResult<PlatformUnit>.Failed(error.Code == OperationNotPermitted ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.IoError, error);
}
