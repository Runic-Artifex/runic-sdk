namespace Runic.Platform.Runtime;

/// <summary>A statically selected file-dialog provider.</summary>
public interface IPickerBackend : IFileDialogs
{
    /// <summary>Whether the provider can currently show dialogs.</summary>
    bool IsAvailable { get; }
    /// <summary>Whether selected save destinations can be staged and atomically replaced.</summary>
    bool SupportsAtomicReplace => true;
    /// <summary>Whether this backend implements local directory selection.</summary>
    /// <remarks>
    /// This reports the backend's <see cref="IFileDialogs.OpenDirectoryAsync"/> implementation before a call; it
    /// does not select behavior. Return <see langword="true"/> exactly when the backend implements that method, and
    /// keep the inherited default (<see langword="false"/>) only when it relies on the default method, which reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>. <see cref="PresentationFiles"/> reports the
    /// <c>platform.directories.open</c> capability from this value and always calls the backend's method; it throws
    /// <see cref="InvalidOperationException"/> when a backend reporting <see langword="false"/> returns anything
    /// other than <see cref="PickerResult{T}.Unavailable"/>, after releasing any selected access.
    /// </remarks>
    bool SupportsDirectorySelection => false;
    /// <summary>Whether this backend implements <see cref="IFileDialogs.OpenFilesAsync"/>.</summary>
    /// <remarks>
    /// <see cref="PresentationFiles"/> reports the <c>platform.files.openMultiple</c> capability from this value and
    /// returns <see cref="PlatformUnavailableReason.BackendUnavailable"/> from <c>OpenFilesAsync</c> without calling
    /// a backend that reports <see langword="false"/>.
    /// </remarks>
    bool SupportsMultipleSelection => false;
    /// <summary>Whether this backend implements <see cref="IFileDialogs.OpenDirectoriesAsync"/>.</summary>
    /// <remarks>
    /// <see cref="PresentationFiles"/> reports the <c>platform.directories.openMultiple</c> capability from this value
    /// and returns <see cref="PlatformUnavailableReason.BackendUnavailable"/> from <c>OpenDirectoriesAsync</c> without
    /// calling a backend that reports <see langword="false"/>.
    /// </remarks>
    bool SupportsMultipleDirectorySelection => false;
    /// <summary>Whether this backend implements <see cref="IFileDialogs.OpenDocumentAsync"/> and
    /// <see cref="IFileDialogs.SaveDocumentAsync"/>.</summary>
    /// <remarks>
    /// <see cref="PresentationFiles"/> reports the <c>platform.files.openDocument</c> capability from this value and
    /// returns <see cref="PlatformUnavailableReason.BackendUnavailable"/> from both methods without calling a backend
    /// that reports <see langword="false"/>.
    /// </remarks>
    bool SupportsDocumentAccess => false;
}

/// <summary>A verified presentation owner. Native handles stay in C# and are usable only during dispatch.</summary>
public interface INativeWindowOwner
{
    /// <summary>The verified presentation generation, shared with its lifetime.</summary>
    Guid Generation { get; }
    /// <summary>Whether the provider can currently show dialogs.</summary>
    bool IsAvailable { get; }
    /// <summary>Revalidates owner identity and dispatches on its native thread. The handle cannot escape the callback.</summary>
    ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default);
}
