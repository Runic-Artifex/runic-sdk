using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Desktop;

/// <summary>Identifies why application close was requested.</summary>
public enum DesktopCloseReason
{
    /// <summary>Application code requested close.</summary>
    Application = 1,
    /// <summary>The user closed the native window.</summary>
    NativeWindow = 2,
    /// <summary>The host is shutting down.</summary>
    HostShutdown = 3,
}

/// <summary>Contains an immutable close request.</summary>
public sealed record DesktopCloseRequest(DesktopCloseReason Reason);

/// <summary>Represents one application close-guard decision.</summary>
public readonly record struct DesktopCloseDecision
{
    private DesktopCloseDecision(bool isAllowed, string? reason)
    {
        if (!isAllowed && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A denied close decision requires a reason.", nameof(reason));
        }

        IsAllowed = isAllowed;
        Reason = reason;
    }

    /// <summary>Gets whether close may proceed.</summary>
    public bool IsAllowed { get; }

    /// <summary>Gets the bounded denial reason.</summary>
    public string? Reason { get; }

    /// <summary>Allows close.</summary>
    public static DesktopCloseDecision Allow() => new(true, null);

    /// <summary>Denies close with a consumer-facing reason.</summary>
    public static DesktopCloseDecision Deny(string reason) => new(false, reason);
}

/// <summary>Allows a testable application service or ViewModel to guard ordinary close.</summary>
public interface IDesktopCloseGuard
{
    /// <summary>Determines whether the application may close.</summary>
    ValueTask<DesktopCloseDecision> CanCloseAsync(
        DesktopCloseRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Coordinates guarded application close and deterministic cancellation.</summary>
public interface IDesktopApplicationLifetime
{
    /// <summary>Gets a token cancelled as soon as application close is accepted or forced.</summary>
    CancellationToken Stopping { get; }

    /// <summary>Registers a guard and returns its exactly-once lifetime lease.</summary>
    IDisposable RegisterCloseGuard(IDesktopCloseGuard guard);

    /// <summary>Requests guarded close of the native application window.</summary>
    ValueTask<DesktopCloseDecision> RequestCloseAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies the requested native-window state.</summary>
public enum DesktopWindowState
{
    /// <summary>Restore ordinary window presentation.</summary>
    Normal = 1,
    /// <summary>Minimize the window.</summary>
    Minimized = 2,
    /// <summary>Maximize the window.</summary>
    Maximized = 3,
}

/// <summary>Contains positive native-window dimensions.</summary>
public readonly record struct DesktopSize
{
    /// <summary>Creates validated dimensions.</summary>
    public DesktopSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
    }

    /// <summary>Gets the width in device-independent pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in device-independent pixels.</summary>
    public int Height { get; }
}

/// <summary>Contains a non-negative native-window position.</summary>
public readonly record struct DesktopPosition
{
    /// <summary>Creates a validated position.</summary>
    public DesktopPosition(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        X = x;
        Y = y;
    }

    /// <summary>Gets the horizontal screen coordinate.</summary>
    public int X { get; }

    /// <summary>Gets the vertical screen coordinate.</summary>
    public int Y { get; }
}

/// <summary>Provides native-window operations without exposing the browser host.</summary>
public interface IDesktopWindow
{
    /// <summary>Brings the owning native window to the foreground.</summary>
    ValueTask FocusAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the outer native-window size.</summary>
    ValueTask SetSizeAsync(DesktopSize size, CancellationToken cancellationToken = default);

    /// <summary>Changes the native-window position.</summary>
    ValueTask SetPositionAsync(DesktopPosition position, CancellationToken cancellationToken = default);

    /// <summary>Centers the native window on its current screen.</summary>
    ValueTask CenterAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the native-window state.</summary>
    ValueTask SetStateAsync(
        DesktopWindowState state,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides semantic document focus without exposing DOM objects.</summary>
public interface IDesktopFocus
{
    /// <summary>Focuses the element with the supplied stable HTML identifier.</summary>
    ValueTask FocusElementAsync(
        string elementId,
        CancellationToken cancellationToken = default);
}

/// <summary>Serializes work onto the owning native UI dispatcher.</summary>
public interface IDesktopDispatcher
{
    /// <summary>Gets whether the caller currently owns dispatcher access.</summary>
    bool CheckAccess();

    /// <summary>Schedules and awaits one UI-affine operation.</summary>
    ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies one semantic keyboard accelerator.</summary>
public sealed record DesktopKeyboardAccelerator
{
    /// <summary>Creates a normalized key combination.</summary>
    public DesktopKeyboardAccelerator(
        string key,
        bool control = false,
        bool alternate = false,
        bool shift = false,
        bool meta = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key.Trim();
        Control = control;
        Alternate = alternate;
        Shift = shift;
        Meta = meta;
    }

    /// <summary>Gets the DOM key identity.</summary>
    public string Key { get; }
    /// <summary>Gets whether Control must be pressed.</summary>
    public bool Control { get; }
    /// <summary>Gets whether Alt/Option must be pressed.</summary>
    public bool Alternate { get; }
    /// <summary>Gets whether Shift must be pressed.</summary>
    public bool Shift { get; }
    /// <summary>Gets whether Command/Meta must be pressed.</summary>
    public bool Meta { get; }
}

/// <summary>Registers semantic keyboard accelerators without exposing browser events.</summary>
public interface IDesktopKeyboardAccelerators
{
    /// <summary>Registers a callback and returns its exactly-once lifetime lease.</summary>
    ValueTask<IDisposable> RegisterAsync(
        DesktopKeyboardAccelerator accelerator,
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides text-only clipboard access.</summary>
public interface IDesktopClipboard
{
    /// <summary>Reads text from the platform clipboard.</summary>
    ValueTask<string?> ReadTextAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes text to the platform clipboard.</summary>
    ValueTask WriteTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Identifies one file type accepted by a file dialog.</summary>
public sealed record DesktopFileType
{
    /// <summary>Creates a named, immutable extension filter.</summary>
    public DesktopFileType(string description, IReadOnlyList<string> extensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(extensions);
        string[] copy = new string[extensions.Count];
        for (int index = 0; index < extensions.Count; index++)
        {
            string extension = extensions[index] ??
                throw new ArgumentException("File extensions cannot contain null.", nameof(extensions));
            if (extension.Length < 2 || extension[0] != '.' || extension.Contains('/', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"File extension '{extension}' must begin with a period and contain no path separator.",
                    nameof(extensions));
            }

            copy[index] = extension;
        }

        Description = description.Trim();
        Extensions = Array.AsReadOnly(copy);
    }

    /// <summary>Gets the consumer-facing type description.</summary>
    public string Description { get; }

    /// <summary>Gets accepted extensions including their leading period.</summary>
    public IReadOnlyList<string> Extensions { get; }
}

/// <summary>Contains immutable open-file dialog options.</summary>
public sealed record DesktopOpenFileOptions(
    string Title,
    IReadOnlyList<DesktopFileType> FileTypes,
    bool AllowMultiple = false);

/// <summary>Contains immutable save-file dialog options.</summary>
public sealed record DesktopSaveFileOptions(
    string Title,
    string SuggestedFileName,
    IReadOnlyList<DesktopFileType> FileTypes);

/// <summary>Contains one browser-safe selected file.</summary>
public sealed record DesktopFile(string Name, string MediaType, ReadOnlyMemory<byte> Content);

/// <summary>Provides browser-safe file selection without returning platform paths.</summary>
public interface IDesktopFileDialogs
{
    /// <summary>Shows an open dialog and returns selected file contents.</summary>
    ValueTask<IReadOnlyList<DesktopFile>> OpenAsync(
        DesktopOpenFileOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Shows a save dialog or browser download prompt.</summary>
    ValueTask<bool> SaveAsync(
        DesktopSaveFileOptions options,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);
}

/// <summary>Contains one bounded drag-and-drop observation.</summary>
public sealed record DesktopDrop(IReadOnlyList<DesktopFile> Files, string? Text);

/// <summary>Publishes drag-and-drop observations.</summary>
public interface IDesktopDropTarget
{
    /// <summary>Occurs after a complete bounded drop has been received.</summary>
    event EventHandler<DesktopDrop>? Dropped;
}

/// <summary>Opens external URIs through platform policy.</summary>
public interface IDesktopExternalLauncher
{
    /// <summary>Opens one absolute HTTP or HTTPS URI outside the application.</summary>
    ValueTask OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

/// <summary>Contains a desktop notification.</summary>
public sealed record DesktopNotification(string Title, string Body, string? Tag = null);

/// <summary>Publishes user-visible desktop notifications.</summary>
public interface IDesktopNotifications
{
    /// <summary>Shows one notification after applying platform permission policy.</summary>
    ValueTask ShowAsync(
        DesktopNotification notification,
        CancellationToken cancellationToken = default);
}

/// <summary>Contains immutable browser profile and storage policy.</summary>
public sealed record DesktopBrowserProfile
{
    /// <summary>Creates a validated browser profile policy.</summary>
    public DesktopBrowserProfile(
        string name,
        string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        if (!System.IO.Path.IsPathFullyQualified(storagePath))
        {
            throw new ArgumentException(
                "A browser profile storage path must be absolute.",
                nameof(storagePath));
        }

        Name = name.Trim();
        StoragePath = System.IO.Path.GetFullPath(storagePath);
    }

    /// <summary>Gets the stable profile name.</summary>
    public string Name { get; }

    /// <summary>Gets the absolute storage directory.</summary>
    public string StoragePath { get; }

}

/// <summary>Exposes the profile selected before the native window started.</summary>
public interface IDesktopBrowserProfile
{
    /// <summary>Gets the selected profile, or null for host-default ephemeral policy.</summary>
    DesktopBrowserProfile? Current { get; }
}

/// <summary>Provides bounded browser-local key/value storage.</summary>
public interface IDesktopBrowserStorage
{
    /// <summary>Reads a value from the selected isolated profile.</summary>
    ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Writes a value to the selected isolated profile.</summary>
    ValueTask WriteAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a value from the selected isolated profile.</summary>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Represents one application-owned secondary window.</summary>
public interface IDesktopOwnedWindow : IAsyncDisposable
{
    /// <summary>Gets the stable application window identifier.</summary>
    string Id { get; }

    /// <summary>Requests deterministic close.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates and owns secondary application windows.</summary>
public interface IDesktopWindowManager
{
    /// <summary>Creates a secondary window using an application-owned entry point.</summary>
    ValueTask<IDesktopOwnedWindow> OpenAsync(
        string id,
        string title,
        Uri entryPoint,
        DesktopSize size,
        CancellationToken cancellationToken = default);
}
