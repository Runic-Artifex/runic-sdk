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

// SMAppService outcomes, without AppKit. The status after a call decides whether the application starts at login;
// an error classifies a call that left the status unchanged.
internal static class MacLoginStartupModel
{
    // SMAppServiceStatus.
    internal const long StatusNotRegistered = 0, StatusEnabled = 1, StatusRequiresApproval = 2, StatusNotFound = 3;
    // ServiceManagement's errors (SMErrors.h), which SMAppService reports in SMAppServiceErrorDomain.
    internal const long ErrorInternalFailure = 2, ErrorInvalidSignature = 3, ErrorAuthorizationFailure = 4,
        ErrorJobNotFound = 6, ErrorServiceUnavailable = 7, ErrorJobPlistNotFound = 8, ErrorLaunchDeniedByUser = 11,
        ErrorAlreadyRegistered = 12;
    // EPERM in NSPOSIXErrorDomain, and the code macOS also reports in SMAppServiceErrorDomain for an operation it
    // does not permit.
    private const long OperationNotPermitted = 1;

    /// <summary>The hint attached to a registration that waits for the user.</summary>
    internal static readonly PlatformDiagnostic ApprovalHint = new("SMAppServiceStatus", StatusRequiresApproval,
        "The login item needs the user's approval in System Settings, General, Login Items.");

    internal static bool IsApplicationBundle(string identifier, string bundlePath) =>
        identifier.Length > 0 && bundlePath.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnregistered(long status) => status is StatusNotRegistered or StatusNotFound;

    /// <summary>Whether the application starts at login. One that waits for approval or that the user turned off does not.</summary>
    internal static PlatformResult<bool> Enabled(long status) => new PlatformResult<bool>.Success(status == StatusEnabled);

    /// <summary>The outcome of registering: enabled, denied until the user approves it, or the call's failure.</summary>
    internal static PlatformResult<PlatformUnit> Registered(long status, PlatformDiagnostic? error) => status switch
    {
        StatusEnabled => new PlatformResult<PlatformUnit>.Success(new PlatformUnit()),
        StatusRequiresApproval => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, ApprovalHint),
        _ when error is { Code: ErrorAlreadyRegistered } => Unchanged(status, "SMAppService reports the login item registered, but its status is not enabled."),
        _ when error is not null => Failed(error),
        _ => Unchanged(status, "SMAppService accepted the registration, but the login item's status did not change."),
    };

    /// <summary>The outcome of unregistering: success once the item is gone.</summary>
    internal static PlatformResult<PlatformUnit> Unregistered(long status, PlatformDiagnostic? error) =>
        IsUnregistered(status) ? new PlatformResult<PlatformUnit>.Success(new PlatformUnit())
        : error is not null && !IsNotFound(error) ? Failed(error)
        : Unchanged(status, "SMAppService accepted the removal, but the login item is still registered.");

    private static bool IsNotFound(PlatformDiagnostic error) => IsServiceManagement(error) && error.Code is ErrorJobNotFound or ErrorJobPlistNotFound;

    private static bool IsServiceManagement(PlatformDiagnostic error) =>
        error.Domain.Contains("SMAppService", StringComparison.Ordinal) || error.Domain.Contains("ServiceManagement", StringComparison.Ordinal);

    /// <summary>Classifies an SMAppService error, with a hint for the ones an application can act on.</summary>
    internal static PlatformResult<PlatformUnit> Failed(PlatformDiagnostic error)
    {
        bool serviceManagement = IsServiceManagement(error);
        bool posix = error.Domain == "NSPOSIXErrorDomain";
        var (code, hint) = error.Code switch
        {
            OperationNotPermitted when serviceManagement || posix => (PlatformFailureCode.PermissionDenied, "macOS did not permit the login item."),
            ErrorLaunchDeniedByUser or ErrorAuthorizationFailure when serviceManagement => (PlatformFailureCode.PermissionDenied, ApprovalHint.Message),
            ErrorInvalidSignature when serviceManagement => (PlatformFailureCode.InvalidData,
                "The application bundle's code signature is invalid; sign the bundle to register it as a login item."),
            ErrorServiceUnavailable or ErrorInternalFailure when serviceManagement => (PlatformFailureCode.Other, "ServiceManagement could not register the login item."),
            _ => (PlatformFailureCode.Other, null),
        };
        return new PlatformResult<PlatformUnit>.Failed(code, error.Message is null && hint is not null ? error with { Message = hint } : error);
    }

    private static PlatformResult<PlatformUnit> Unchanged(long status, string message) =>
        new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, new PlatformDiagnostic("SMAppServiceStatus", status, message));
}
