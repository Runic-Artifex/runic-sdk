using Runic.Platform.Runtime;
using System.Threading.Channels;
using Runic.Platform.Linux.DBus;
using System.Text;

namespace Runic.Platform.Linux.Portal;

// The portal requests the services of PortalApplication share. Internal: PortalApplication is the package's entry point.
internal static class PortalPlatformProvider
{
    internal static async ValueTask<PlatformResult<PlatformUnit>> OpenUriCoreAsync(IPortalWindowOwner owner, Uri uri, IPortalTransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        string target = DesktopUriPolicy.Require(uri);
        // ms-settings: opens Windows Settings; the portal has no handler for it.
        if (DesktopUriPolicy.IsWindowsOnly(uri))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        try
        {
            var response = await PortalRequest.RunAsync(owner, transport, "OpenURI", target, cancellationToken).ConfigureAwait(false);
            return response.Code switch
            {
                0 => new PlatformResult<PlatformUnit>.Success(new PlatformUnit()),
                // The user cancelled the portal's application chooser.
                1 => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.UserDismissed, response.Diagnostic),
                _ => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, response.Diagnostic),
            };
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
    // Shorthands for the tests.
    internal ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(CancellationToken cancellationToken) =>
        SelectDirectoryAsync(null, cancellationToken);

    // Directory selection needs FileChooser version 3 on the session bus. ProbeDirectorySelectionAsync reads the version
    // when the dialogs are created, so the capability reports a missing portal before the first pick; a pick checks
    // again while it is missing, and its outcome updates the state.
    private const int DirectoriesUnknown = 0, DirectoriesAvailable = 1, DirectoriesMissing = 2;
    private int _directories;

    public bool IsDirectorySelectionAvailable => Volatile.Read(ref _directories) != DirectoriesMissing;

    internal async Task ProbeDirectorySelectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await transport.GetFileChooserVersionAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _directories, version >= 3 ? DirectoriesAvailable : DirectoriesMissing);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // No session bus, no portal on it, or a portal without FileChooser: a pick would report BackendUnavailable.
        catch (Exception) { Volatile.Write(ref _directories, DirectoriesMissing); }
    }

    private async Task<PortalResponse> SelectDirectoriesCoreAsync(string? initialDirectory, bool multiple, CancellationToken cancellationToken,
        string? title = null)
    {
        if (Volatile.Read(ref _directories) == DirectoriesMissing)
        {
            await ProbeDirectorySelectionAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _directories) == DirectoriesMissing) throw new NativeBackendUnavailableException();
        }
        try
        {
            var result = await PortalRequest.RunAsync(owner, transport, "SelectDirectory", "", cancellationToken, diagnosticSink,
                PortalChooserOptions.From([], initialDirectory, multiple, title)).ConfigureAwait(false);
            Volatile.Write(ref _directories, DirectoriesAvailable);
            return result;
        }
        catch (NativeBackendUnavailableException)
        {
            Volatile.Write(ref _directories, DirectoriesMissing);
            throw;
        }
    }

    public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken) =>
        ((INativeDirectoryPicker)this).SelectDirectoryAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory }, cancellationToken);

    async ValueTask<NativeDirectorySelection?> INativeDirectoryPicker.SelectDirectoryAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await SelectDirectoriesCoreAsync(request.InitialDirectory, multiple: false, cancellationToken, request.Title).ConfigureAwait(false);
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

    // FileChooser's directory and multiple options; SelectDirectory checks for version 3, which added directory.
    public bool SupportsMultipleDirectorySelection => true;

    public ValueTask<IReadOnlyList<NativeDirectorySelection>?> SelectDirectoriesAsync(string? initialDirectory, CancellationToken cancellationToken) =>
        ((INativeDirectoryPicker)this).SelectDirectoriesAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory }, cancellationToken);

    async ValueTask<IReadOnlyList<NativeDirectorySelection>?> INativeDirectoryPicker.SelectDirectoriesAsync(NativeFileDialogRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await SelectDirectoriesCoreAsync(request.InitialDirectory, multiple: true, cancellationToken, request.Title).ConfigureAwait(false);
        if (result.Code == 1) return null;
        if (result.Code != 0) throw new NativeBackendUnavailableException();
        if (result.Uris.Length == 0) throw new IOException("The portal did not return a local directory.");
        return [.. result.Uris.Select(static uri => new NativeDirectorySelection(ReadExactLocalPath(uri), null))];
    }

    internal async ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken) =>
        (await SelectAsync(new NativeFileDialogRequest(save, suggestedName), cancellationToken).ConfigureAwait(false)) is [var selection] ? selection : null;

    public async ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool save = request.Save, multiple = request.AllowMultiple && !save;
        var result = await PortalRequest.RunAsync(owner, transport, save ? "SaveFile" : "OpenFile", request.SuggestedName ?? "Untitled", cancellationToken,
            diagnosticSink, PortalChooserOptions.From(request.Filters, request.InitialDirectory, multiple, request.Title)).ConfigureAwait(false);
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
            // Saves and opened documents both write back: a document view stages through the portal.
            var location = await (documents ?? DocumentPortalPaths.Session).ClassifyAsync(uri.LocalPath, cancellationToken).ConfigureAwait(false);
            selections.Add(new NativeFileSelection(uri.LocalPath, null, AllowsSiblingReplacement: true) { Location = location });
        }
        if (PortalChooserOptions.Chosen(request.Filters, result.CurrentFilter) is { } chosen) selections[0] = selections[0] with { SelectedFilter = chosen };
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
        catch (Exception error) when (error is DBusException or TimeoutException or DllNotFoundException or EntryPointNotFoundException or NativeBackendUnavailableException or ChannelClosedException)
        {
            var diagnostic = parentExported
                ? PortalDiagnostic.Create("portal-request-unavailable", "The desktop portal could not complete the request.", "Ensure the session D-Bus service and a desktop-appropriate xdg-desktop-portal backend are running.")
                : PortalDiagnostic.Create("portal-parent-unavailable", "The presentation could not export an owned portal parent.", "Use a supported X11 or Wayland session and a live owner. Hostless applications may explicitly choose unparented dialogs.");
            try { diagnosticSink?.Invoke(diagnostic); } catch { /* Observers cannot replace the platform outcome. */ }
            throw new NativeBackendUnavailableException();
        }
    }
}
