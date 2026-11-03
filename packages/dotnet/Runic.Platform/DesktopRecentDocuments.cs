namespace Runic.Platform;

/// <summary>
/// The application's recent documents, which the desktop lists for reopening: Open Recent and the Dock menu on macOS,
/// the Jump List and Recent items on Windows, and the recent files of file choosers and file managers on Linux.
/// </summary>
/// <remarks>
/// <para>
/// An application-scoped service that needs no window; a ViewModel takes it in its constructor. Note a document after
/// the user opened it and after Save As. A path is a fully qualified local path, supplied by trusted application code.
/// </para>
/// <para>
/// macOS: <c>NSDocumentController</c> on the main thread, which Runic Desktop's event loop serves; in the App Sandbox,
/// note the path while the document's lease is open. Windows: <c>SHAddToRecentDocs</c>, which also fills the Recent
/// category of the application's Jump List where the application is registered for the file type, and
/// <see cref="ClearAsync"/> removes the application's Jump List destinations without touching other applications'.
/// Linux: the freedesktop.org <c>recently-used.xbel</c> file that GTK and KDE read, written on the thread pool, with this
/// application, named by its application ID or else its entry assembly, as the document's application;
/// <see cref="ClearAsync"/> removes this application from every entry and the entries only it had. A write keeps the
/// newest 500 entries, and a file that is not a bookmark file is left as it is and reported as
/// <see cref="PlatformFailureCode.IOError"/>. Inside Flatpak that file is the application's own. A path is made
/// absolute before it is noted. Hosts without a desktop, such as CS-WebUI and serve-only
/// Runic Desktop, report <see cref="PlatformResult{T}.Unavailable"/>.
/// </para>
/// </remarks>
public interface IDesktopRecentDocuments
{
    /// <summary>Adds the document at <paramref name="path"/> to the recent documents, or moves it to the top.</summary>
    /// <param name="path">A fully qualified local path to the document.</param>
    /// <param name="cancellationToken">Cancels the request before it is made.</param>
    /// <returns>Success, or the platform's failure, such as <see cref="PlatformFailureCode.IOError"/> when the list cannot be written.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, not fully qualified, or contains a NUL character.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    ValueTask<PlatformResult<PlatformUnit>> AddAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Empties the application's recent documents, as Clear Menu in Open Recent does.</summary>
    /// <param name="cancellationToken">Cancels the request before it is made.</param>
    ValueTask<PlatformResult<PlatformUnit>> ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Recent documents for hosts and platforms without a provider.</summary>
public static class DesktopRecentDocuments
{
    /// <summary>Creates recent documents that report every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Nothing throws except for invalid arguments and caller cancellation.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopRecentDocuments Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableRecentDocuments(reason);
    }

    // The argument checks of IDesktopRecentDocuments.AddAsync.
    internal static void ValidatePath(string path, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        if (path.Length == 0) throw new ArgumentException("The document path is empty.", parameterName);
        if (path.Contains('\0')) throw new ArgumentException("The document path contains a NUL character.", parameterName);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The document path is not fully qualified.", parameterName);
    }

    private sealed class UnavailableRecentDocuments(PlatformUnavailableReason reason) : IDesktopRecentDocuments, IDesktopServiceStatus
    {
        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);

        public ValueTask<PlatformResult<PlatformUnit>> AddAsync(string path, CancellationToken cancellationToken = default)
        {
            ValidatePath(path, nameof(path));
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(reason));
        }

        public ValueTask<PlatformResult<PlatformUnit>> ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(reason));
        }
    }
}
