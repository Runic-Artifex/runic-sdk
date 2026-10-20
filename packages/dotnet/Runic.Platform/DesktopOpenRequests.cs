using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>Files and URLs the desktop asked the application to open, in the order it gave them.</summary>
/// <param name="Items">
/// Absolute URIs: <c>file:</c> URIs for files, and URIs with one of the application's registered schemes for URLs.
/// </param>
/// <remarks>
/// Treat every item as untrusted input at the application boundary: any website or local program can open a URL with
/// the application's scheme, and a file can come from anywhere. Validate a URL before acting on it, and confirm
/// anything destructive with the user.
/// </remarks>
public sealed record DesktopOpenRequest(ImmutableArray<Uri> Items)
{
    /// <summary>Short-lived desktop activation data for raising a window. Dispatch promptly; do not persist it.</summary>
    public DesktopActivationContext? PlatformContext { get; init; }
}

/// <summary>The URL schemes and files the application registered with the desktop.</summary>
/// <param name="UrlSchemes">
/// URL schemes, such as <c>org.example.notes</c>, as registered in the desktop entry, <c>Info.plist</c> or the Windows
/// registry. Compared without regard to case.
/// </param>
/// <remarks>Only registered schemes are delivered, so an argument that merely looks like a URL is left to the application.</remarks>
public sealed record DesktopOpenOptions(ImmutableArray<string> UrlSchemes)
{
    /// <summary>Whether the application registered file types, so that file paths and <c>file:</c> URIs are delivered.</summary>
    public bool IncludeFiles { get; init; }
}

/// <summary>Requests to open files and URLs, from this launch, later launches and, on macOS, the system's open events.</summary>
/// <remarks>Dispose it when the application ends; later requests are then dropped.</remarks>
public interface IDesktopOpenRequests : IAsyncDisposable
{
    /// <summary>
    /// Raised for each request on a worker thread, one at a time and in order; dispatch window work through the
    /// application's host. Requests that arrive before the first handler subscribes wait for it.
    /// </summary>
    event EventHandler<DesktopOpenRequest>? Opened;
}

/// <summary>Open requests for hosts and platforms that cannot provide them, and the parser for launch arguments.</summary>
public static class DesktopOpenRequests
{
    private const int MaximumItems = 1024;
    private const int MaximumLength = 32 * 1024;
    private static readonly System.Buffers.SearchValues<char> SchemeCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.");

    /// <summary>Creates open requests that never raise <see cref="IDesktopOpenRequests.Opened"/>.</summary>
    /// <remarks>The reason documents why; nothing reports it, because no request ever arrives.</remarks>
    public static IDesktopOpenRequests Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableOpenRequests();
    }

    /// <summary>
    /// The files and URLs among a launch's arguments: the desktop passes them as arguments on Linux and Windows.
    /// </summary>
    /// <returns>The request, or <see langword="null"/> when no argument is a registered URL or, with
    /// <see cref="DesktopOpenOptions.IncludeFiles"/>, a file.</returns>
    /// <remarks>
    /// An argument is a URL when it is an absolute URI with one of <see cref="DesktopOpenOptions.UrlSchemes"/>. With
    /// <see cref="DesktopOpenOptions.IncludeFiles"/>, an argument is a file when it is a <c>file:</c> URI, or a path of an
    /// existing file or directory that does not start with <c>-</c>; a relative path is resolved against the launch's
    /// working directory. Every other
    /// argument, such as an option, is ignored and stays with the application's own parser. A URL is never read as a
    /// path. At most 1024 items of at most 32 KiB each are taken.
    /// </remarks>
    public static DesktopOpenRequest? FromLaunch(ApplicationLaunch launch, DesktopOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ValidateOptions(options);
        var items = ImmutableArray.CreateBuilder<Uri>();
        if (!launch.Arguments.IsDefault)
        {
            foreach (string argument in launch.Arguments)
            {
                if (items.Count == MaximumItems) break;
                if (Parse(argument, launch.WorkingDirectory, options) is { } item) items.Add(item);
            }
        }
        return items.Count == 0 ? null : new DesktopOpenRequest(items.ToImmutable()) { PlatformContext = launch.PlatformContext };
    }

    private static void ValidateOptions(DesktopOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.UrlSchemes.IsDefault) throw new ArgumentException("Supply the registered URL schemes, or none.", nameof(options));
        foreach (string scheme in options.UrlSchemes)
            if (!IsScheme(scheme) || scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{scheme}' is not a URL scheme an application registers.", nameof(options));
    }

    private static Uri? Parse(string argument, string workingDirectory, DesktopOpenOptions options)
    {
        if (string.IsNullOrEmpty(argument) || argument.Length > MaximumLength || argument.Contains('\0')) return null;
        int colon = argument.IndexOf(':', StringComparison.Ordinal);
        // A scheme has at least two characters, so "C:\..." is a Windows path, not a URI.
        if (colon > 1 && IsScheme(argument[..colon]))
        {
            string scheme = argument[..colon];
            if (scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                return options.IncludeFiles && Uri.TryCreate(argument, UriKind.Absolute, out var file) && file.IsFile ? file : null;
            return options.UrlSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase)
                && Uri.TryCreate(argument, UriKind.Absolute, out var url) ? url : null;
        }
        if (!options.IncludeFiles || argument.StartsWith('-')) return null;
        try
        {
            string path = Path.GetFullPath(argument, Path.IsPathFullyQualified(workingDirectory) ? workingDirectory : Environment.CurrentDirectory);
            // Only an existing file or directory, so an option's value, such as a profile name, is not taken for one.
            return File.Exists(path) || Directory.Exists(path) ? new Uri(path, UriKind.Absolute) : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException) { return null; }
    }

    // RFC 3986: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ).
    private static bool IsScheme(string value) =>
        value.Length is > 0 and <= 64 && char.IsAsciiLetter(value[0])
        && value.AsSpan(1).IndexOfAnyExcept(SchemeCharacters) < 0;

    private sealed class UnavailableOpenRequests : IDesktopOpenRequests
    {
        public event EventHandler<DesktopOpenRequest>? Opened { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
