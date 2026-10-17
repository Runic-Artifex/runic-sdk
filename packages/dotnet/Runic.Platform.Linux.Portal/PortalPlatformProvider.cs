using Runic.Platform.Runtime;
using System.Threading.Channels;
using Tmds.DBus.Protocol;
using System.Text;

namespace Runic.Platform.Linux.Portal;

/// <summary>Direct desktop portal services, independent of GTK version.</summary>
public static class PortalPlatformProvider
{
    /// <summary>Creates application-scoped appearance preferences without selecting a GTK toolkit.</summary>
    public static IDesktopSettings CreateSettings() => new PortalDesktopSettings();
    /// <summary>Creates application-scoped notifications. Disposal leaves delivered notifications in the desktop.</summary>
    /// <remarks>A supplied host ID requires a matching installed desktop entry and the host Registry portal. Diagnostic callbacks never receive notification text.</remarks>
    public static IDesktopNotifications CreateNotifications(string? applicationId = null, Action<PortalDiagnostic>? diagnosticSink = null) =>
        new PortalApplication(applicationId, diagnosticSink).CreateNotifications();
    /// <summary>Creates a status icon with a menu, registered with the desktop's StatusNotifierWatcher.</summary>
    /// <remarks>Plasma, Waybar's tray and GNOME's AppIndicator extension show it; elsewhere it reports unavailable
    /// and appears once a status area starts.</remarks>
    public static IDesktopStatusIcon CreateStatusIcon(Action<PortalDiagnostic>? diagnosticSink = null) =>
        new PortalApplication(null, diagnosticSink).CreateStatusIcon();
    /// <summary>Creates login startup for the application with the installed desktop ID <paramref name="applicationId"/>.</summary>
    /// <remarks>Without an ID it works only inside Flatpak or Snap, where the portal knows the application.</remarks>
    public static IDesktopLoginStartup CreateLoginStartup(string? applicationId = null, Action<PortalDiagnostic>? diagnosticSink = null) =>
        new PortalApplication(applicationId, diagnosticSink).CreateLoginStartup();
    /// <summary>Creates owned local-file handoff operations.</summary>
    public static IDesktopFileLauncher CreateFileLauncher(IPortalWindowOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); return new PortalFileLauncher(owner); }

    /// <summary>Creates portal-only file dialogs bound to a verified presentation.</summary>
    public static IPickerBackend CreateFileDialogs(IPortalWindowOwner owner, Action<PortalDiagnostic>? diagnosticSink = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NativePickerBackend(owner, new PortalFilePicker(owner, new PortalTransport(), diagnosticSink));
    }

    /// <summary>Explicitly creates unparented dialogs for an application with no native presentation.</summary>
    public static IPickerBackend CreateUnparentedFileDialogs() => CreateFileDialogs(new UnparentedOwner());

    /// <summary>Asks the desktop to open an HTTP, HTTPS or mail URI for this presentation.</summary>
    public static ValueTask<PlatformResult<PlatformUnit>> OpenUriAsync(IPortalWindowOwner owner, Uri uri, CancellationToken cancellationToken = default) =>
        OpenUriCoreAsync(owner, uri, null, cancellationToken);

    internal static async ValueTask<PlatformResult<PlatformUnit>> OpenUriCoreAsync(IPortalWindowOwner owner, Uri uri, PortalApplication? application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto"))
            throw new ArgumentException("Only absolute HTTP, HTTPS and mailto URIs are supported. Local files require a file-descriptor portal request.", nameof(uri));
        try
        {
            var response = await PortalRequest.RunAsync(owner, new PortalTransport(application: application), "OpenURI", uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            return response.Code == 0 ? new PlatformResult<PlatformUnit>.Success(new PlatformUnit()) : new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError, response.Diagnostic);
        }
        catch (OwnerClosedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
    }

    internal sealed class UnparentedOwner : IExplicitUnparentedOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PortalParentLease> ExportParentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<PortalParentLease>(new UnparentedLease());
    }
    private sealed class UnparentedLease : PortalParentLease
    {
        public override string Identifier => "";
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class PortalFilePicker(IPortalWindowOwner owner, IPortalTransport transport, Action<PortalDiagnostic>? diagnosticSink = null,
    DocumentPortalPaths? documents = null) : INativeFilePicker, INativeDirectoryPicker
{
    // A host path stages beside the file as usual. A document view turns the
    // staging name into a private host tempfile and renames it onto the document.
    // A read-only or revoked grant is reported per save, not as a capability.
    public bool SupportsAtomicReplace => true;
    public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(CancellationToken cancellationToken) =>
        SelectDirectoryAsync(null, cancellationToken);

    public async ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken)
    {
        var result = await PortalRequest.RunAsync(owner, transport, "SelectDirectory", "", cancellationToken, diagnosticSink,
            PortalChooserOptions.From([], initialDirectory, multiple: false)).ConfigureAwait(false);
        if (result.Code == 1) return null;
        if (result.Code != 0) throw new NativeBackendUnavailableException();
        if (result.Uris.Length != 1) throw new IOException("The portal did not return one local directory.");
        // Portal document grants belong to the user's permission store. The
        // selected directory remains accessible; disposing our lease does not
        // revoke a user's persistent portal grant.
        return new(ReadExactLocalPath(result.Uris[0]), null);
    }

    internal static string ReadExactLocalPath(string value)
    {
        if (!value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || value.IndexOfAny(['?', '#', '\\', '\0', '\r', '\n']) >= 0)
            throw new IOException("The portal did not return a local directory URI.");
        int pathStart = value.IndexOf('/', 7);
        if (pathStart < 0) throw new IOException("The portal did not return a local directory URI.");
        string host = value[7..pathStart];
        if (host.Length != 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The portal did not return a local directory URI.");
        // Decode the original URI bytes, not Uri.LocalPath: URI normalization
        // or a replacement decoder must not change the selected path identity.
        var utf8 = new UTF8Encoding(false, true);
        var bytes = new List<byte>();
        try
        {
            for (int index = pathStart; index < value.Length;)
            {
                if (value[index] == '%')
                {
                    if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out byte decoded))
                        throw new IOException("The portal returned an invalid URI escape.");
                    bytes.Add(decoded);
                    index += 3;
                }
                else
                {
                    int end = value.IndexOf('%', index);
                    if (end < 0) end = value.Length;
                    bytes.AddRange(utf8.GetBytes(value[index..end]));
                    index = end;
                }
            }
            string path = utf8.GetString(bytes.ToArray());
            if (path.Contains('\0')) throw new IOException("The portal returned an invalid path.");
            return path;
        }
        catch (Exception error) when (error is DecoderFallbackException or EncoderFallbackException)
        { throw new IOException("The native path cannot be represented exactly in C#.", error); }
    }
    public bool SupportsMultipleSelection => true;

    public async ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken) =>
        (await SelectAsync(new NativeFileDialogRequest(save, suggestedName), cancellationToken).ConfigureAwait(false)) is [var selection] ? selection : null;

    public async ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool save = request.Save, multiple = request.AllowMultiple && !save;
        var result = await PortalRequest.RunAsync(owner, transport, save ? "SaveFile" : "OpenFile", request.SuggestedName ?? "Untitled", cancellationToken,
            diagnosticSink, PortalChooserOptions.From(request.Filters, request.InitialDirectory, multiple)).ConfigureAwait(false);
        if (result.Code == 1) return null;
        if (result.Code != 0) throw new NativeBackendUnavailableException();
        if (result.Uris.Length == 0 || !multiple && result.Uris.Length != 1)
            throw new IOException("The portal did not return one local file.");
        var selections = new List<NativeFileSelection>(result.Uris.Length);
        foreach (string value in result.Uris)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || !uri.IsFile || !string.IsNullOrEmpty(uri.Host) && uri.Host != "localhost"
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.LocalPath.Contains('\0'))
                throw new IOException("The portal did not return a local file.");
            if (!save) { selections.Add(new NativeFileSelection(uri.LocalPath, null, AllowsSiblingReplacement: true)); continue; }
            var location = await (documents ?? DocumentPortalPaths.Session).ClassifyAsync(uri.LocalPath, cancellationToken).ConfigureAwait(false);
            selections.Add(new NativeFileSelection(uri.LocalPath, null, AllowsSiblingReplacement: true) { Location = location });
        }
        return selections;
    }
}

internal interface IExplicitUnparentedOwner : IPortalWindowOwner;

internal static class PortalRequest
{
    internal static async ValueTask<PortalResponse> RunAsync(IPortalWindowOwner owner, IPortalTransport transport,
        string method, string argument, CancellationToken cancellationToken, Action<PortalDiagnostic>? diagnosticSink = null,
        PortalChooserOptions? chooser = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable) throw new OwnerClosedException();
        var generation = owner.Generation;
        bool parentExported = false;
        try
        {
            await using var parent = await owner.ExportParentAsync(cancellationToken).ConfigureAwait(false);
            if (owner is not IExplicitUnparentedOwner &&
                !(parent.Identifier.StartsWith("x11:", StringComparison.Ordinal) && parent.Identifier.Length > 4
                  || parent.Identifier.StartsWith("wayland:", StringComparison.Ordinal) && parent.Identifier.Length > 8))
                throw new NativeBackendUnavailableException();
            if (!owner.IsAvailable || owner.Generation != generation || parent.OwnerClosed.IsCancellationRequested)
                throw new OwnerClosedException();
            parentExported = true;
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, parent.OwnerClosed);
            using var monitor = new Timer(_ =>
            {
                if (!owner.IsAvailable || owner.Generation != generation) stopped.Cancel();
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
            try
            {
                var response = await transport.RequestAsync(parent.Identifier, method, argument, chooser, stopped.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!owner.IsAvailable || owner.Generation != generation || parent.OwnerClosed.IsCancellationRequested) throw new OwnerClosedException();
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                && (!owner.IsAvailable || owner.Generation != generation || parent.OwnerClosed.IsCancellationRequested))
            { throw new OwnerClosedException(); }
            finally { await monitor.DisposeAsync().ConfigureAwait(false); }
        }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or DllNotFoundException or EntryPointNotFoundException or NativeBackendUnavailableException or ChannelClosedException)
        {
            var diagnostic = parentExported
                ? new PortalDiagnostic("portal-request-unavailable", "The desktop portal could not complete the request.", "Ensure the session D-Bus service and a desktop-appropriate xdg-desktop-portal backend are running.")
                : new PortalDiagnostic("portal-parent-unavailable", "The presentation could not export an owned portal parent.", "Use a supported X11 or Wayland session and a live owner. Hostless applications may explicitly choose unparented dialogs.");
            try { diagnosticSink?.Invoke(diagnostic); } catch { /* Observers cannot replace the platform outcome. */ }
            throw new NativeBackendUnavailableException();
        }
    }
}
