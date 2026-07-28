using System;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Desktop;

namespace WebUIToolkit.Hosting;

/// <summary>Contains immutable configuration used to create a browser host.</summary>
public sealed record BrowserHostOptions
{
    /// <summary>Initializes browser-host configuration.</summary>
    /// <param name="applicationId">A stable, safe identifier for the hosted application.</param>
    public BrowserHostOptions(string applicationId)
    {
        ApplicationId = BrowserContractValidation.NormalizeIdentifier(applicationId, nameof(applicationId));
    }

    /// <summary>Gets the stable identifier used for diagnostics and runtime isolation.</summary>
    public string ApplicationId { get; }
}

/// <summary>Contains immutable configuration used to create a browser window.</summary>
public sealed record BrowserWindowOptions
{
    /// <summary>Initializes browser-window configuration.</summary>
    /// <param name="windowId">A stable, safe identifier for the window.</param>
    /// <param name="title">The initial window title.</param>
    /// <param name="width">The initial client width in device-independent pixels.</param>
    /// <param name="height">The initial client height in device-independent pixels.</param>
    /// <param name="isResizable">Whether the user may resize the window.</param>
    /// <param name="browserProfile">Optional profile/storage policy applied before startup.</param>
    public BrowserWindowOptions(
        string windowId,
        string title,
        int width = 1024,
        int height = 768,
        bool isResizable = true,
        DesktopBrowserProfile? browserProfile = null)
    {
        WindowId = BrowserContractValidation.NormalizeIdentifier(windowId, nameof(windowId));

        ArgumentNullException.ThrowIfNull(title);
        Title = title.Trim();
        if (Title.Length == 0)
        {
            throw new ArgumentException("The window title cannot be empty or whitespace.", nameof(title));
        }

        foreach (var character in Title)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("The window title cannot contain control characters.", nameof(title));
            }
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Window width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Window height must be positive.");
        }

        Width = width;
        Height = height;
        IsResizable = isResizable;
        BrowserProfile = browserProfile;
    }

    /// <summary>Gets the stable identifier used for diagnostics.</summary>
    public string WindowId { get; }

    /// <summary>Gets the initial window title.</summary>
    public string Title { get; }

    /// <summary>Gets the initial client width in device-independent pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the initial client height in device-independent pixels.</summary>
    public int Height { get; }

    /// <summary>Gets whether the user may resize the window.</summary>
    public bool IsResizable { get; }

    /// <summary>Gets the browser profile/storage policy selected before startup.</summary>
    public DesktopBrowserProfile? BrowserProfile { get; }
}

/// <summary>Creates an isolated browser host without exposing a native runtime.</summary>
public interface IBrowserHostFactory
{
    /// <summary>Creates one uninitialized browser host.</summary>
    ValueTask<IBrowserHost> CreateAsync(
        BrowserHostOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Owns an initialized browser runtime and its windows.</summary>
public interface IBrowserHost : IAsyncDisposable
{
    /// <summary>Gets the dispatcher for browser-affine work.</summary>
    IUiDispatcher Dispatcher { get; }

    /// <summary>Initializes the underlying runtime without creating a window.</summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Creates one hidden browser window.</summary>
    ValueTask<IBrowserWindow> CreateWindowAsync(
        BrowserWindowOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Represents a browser window without exposing its native handle or implementation.</summary>
public interface IBrowserWindow : IAsyncDisposable
{
    /// <summary>
    /// Occurs when the user or browser runtime requests close. Handlers must only signal
    /// lifecycle work and must not run application logic on the callback thread.
    /// </summary>
    event EventHandler? CloseRequested;

    /// <summary>Navigates the window to a validated host-owned entry point.</summary>
    ValueTask NavigateAsync(Uri entryPoint, CancellationToken cancellationToken);

    /// <summary>Shows the window after navigation and session activation.</summary>
    ValueTask ShowAsync(CancellationToken cancellationToken);

    /// <summary>Asynchronously waits until the window has closed.</summary>
    Task WaitForCloseAsync(CancellationToken cancellationToken);

    /// <summary>Requests an idempotent window close.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken);
}

/// <summary>Serializes browser-affine work without exposing a synchronization primitive.</summary>
public interface IUiDispatcher
{
    /// <summary>Gets whether the caller currently has dispatcher access.</summary>
    bool CheckAccess();

    /// <summary>Schedules and awaits browser-affine work.</summary>
    ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken);

    /// <summary>Schedules and awaits browser-affine work that produces a result.</summary>
    ValueTask<TResult> InvokeAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> callback,
        CancellationToken cancellationToken);
}

internal static class BrowserContractValidation
{
    internal static string NormalizeIdentifier(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("An identifier cannot be empty or whitespace.", parameterName);
        }

        if (normalized.Length > 128
            || !char.IsAsciiLetterOrDigit(normalized[0])
            || !char.IsAsciiLetterOrDigit(normalized[^1])
            || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An identifier must be 1-128 characters, begin and end with an ASCII letter or digit, and cannot contain consecutive periods.",
                parameterName);
        }

        foreach (var character in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_')
            {
                throw new ArgumentException(
                    "An identifier can contain only ASCII letters, digits, periods, hyphens, and underscores.",
                    parameterName);
            }
        }

        return normalized;
    }
}
