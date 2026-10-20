namespace Runic.Platform.Runtime;

/// <summary>The native presentation owner is no longer usable.</summary>
/// <remarks>An <see cref="INativeWindowOwner"/> throws it from <see cref="INativeWindowOwner.InvokeAsync"/> once its
/// window has closed.</remarks>
public sealed class OwnerClosedException : InvalidOperationException
{
    /// <summary>Creates the exception with the default message.</summary>
    public OwnerClosedException() : base("The presentation owner has closed.") { }
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public OwnerClosedException(string? message) : base(message) { }
    /// <summary>Creates the exception with <paramref name="message"/> and the native error that caused it.</summary>
    public OwnerClosedException(string? message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The selected native backend cannot serve this environment.</summary>
/// <remarks>An <see cref="INativeFilePicker"/> throws it to report that it cannot show a dialog;
/// <see cref="NativePickerBackend"/> reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</remarks>
public sealed class NativeBackendUnavailableException : Exception
{
    /// <summary>Creates the exception with the default message.</summary>
    public NativeBackendUnavailableException() : base("The native backend cannot serve this environment.") { }
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public NativeBackendUnavailableException(string? message) : base(message) { }
    /// <summary>Creates the exception with <paramref name="message"/> and the native error that caused it.</summary>
    public NativeBackendUnavailableException(string? message, Exception? innerException) : base(message, innerException) { }
}
