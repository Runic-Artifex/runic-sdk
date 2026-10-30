namespace Runic.Platform.Administration.Windows;

/// <summary>Classification of an operational Windows administration failure.</summary>
/// <remarks>
/// This set is open: a minor release may add categories, so handle unknown values. Zero is never a real
/// failure category; <see cref="Unknown"/> is only the default value and the package never reports it.
/// <see cref="NativeFailure"/> is the category for native errors that no other category describes.
/// </remarks>
public enum AdministrationErrorCategory
{
    /// <summary>No category; the default value. Windows administration failures never report it.</summary>
    Unknown = 0,
    /// <summary>Windows denied access.</summary>
    AccessDenied = 1,
    /// <summary>The named target, such as a service, share, file, directory object, task or WMI instance, does not exist.</summary>
    NotFound = 2,
    /// <summary>The target machine, endpoint or service is unreachable or not running.</summary>
    /// <remarks>A component that is not installed is <see cref="NotInstalled"/>.</remarks>
    Unavailable = 3,
    /// <summary>The requested operation conflicts with current state.</summary>
    Conflict = 4,
    /// <summary>Windows returned invalid or unsupported data.</summary>
    InvalidData = 5,
    /// <summary>A native operation failed for a reason no other category describes.</summary>
    NativeFailure = 6,
    /// <summary>A required component is not installed or registered, such as the Group Policy Management Console
    /// (class not registered) or a WMI provider (missing namespace or class).</summary>
    /// <remarks>Distinct from <see cref="Unavailable"/>, which means the component is installed but cannot be reached.</remarks>
    NotInstalled = 7
}

/// <summary>The domain defining a native error number.</summary>
public enum NativeErrorDomain
{
    /// <summary>A Win32 error number.</summary>
    Win32,
    /// <summary>A COM HRESULT.</summary>
    HResult,
    /// <summary>An LDAP result.</summary>
    Ldap
}

/// <summary>An operational failure retaining the original Windows error identity.</summary>
public class WindowsAdministrationException : Exception
{
    /// <summary>Creates an administration failure. Do not include credentials in operation or message.</summary>
    public WindowsAdministrationException(string operation, AdministrationErrorCategory category,
        NativeErrorDomain nativeErrorDomain, int nativeErrorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operation = operation;
        Category = category;
        NativeErrorDomain = nativeErrorDomain;
        NativeErrorCode = nativeErrorCode;
        if (nativeErrorDomain == NativeErrorDomain.HResult) HResult = nativeErrorCode;
    }

    /// <summary>The failed operation, without credentials or connection secrets.</summary>
    public string Operation { get; }
    /// <summary>The classified failure.</summary>
    public AdministrationErrorCategory Category { get; }
    /// <summary>The domain of the native error.</summary>
    public NativeErrorDomain NativeErrorDomain { get; }
    /// <summary>The unmodified native error number.</summary>
    public int NativeErrorCode { get; }
}
