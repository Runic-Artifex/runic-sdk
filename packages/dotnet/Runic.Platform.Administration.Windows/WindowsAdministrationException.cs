namespace Runic.Platform.Administration.Windows;

/// <summary>Classification of an operational Windows administration failure.</summary>
public enum AdministrationErrorCategory
{
    /// <summary>Windows denied access.</summary>
    AccessDenied,
    /// <summary>The target or required component is unavailable.</summary>
    Unavailable,
    /// <summary>The requested operation conflicts with current state.</summary>
    Conflict,
    /// <summary>Windows returned invalid or unsupported data.</summary>
    InvalidData,
    /// <summary>A native operation failed for another reason.</summary>
    NativeFailure
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
