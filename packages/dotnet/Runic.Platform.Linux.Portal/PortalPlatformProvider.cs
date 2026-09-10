using Runic.Platform.Runtime;
using System.Threading.Channels;
using Tmds.DBus.Protocol;

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
    public static ValueTask<PlatformResult<Unit>> OpenUriAsync(IPortalWindowOwner owner, Uri uri, CancellationToken cancellationToken = default) =>
        OpenUriCoreAsync(owner, uri, null, cancellationToken);

    internal static async ValueTask<PlatformResult<Unit>> OpenUriCoreAsync(IPortalWindowOwner owner, Uri uri, PortalApplication? application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto"))
            throw new ArgumentException("Only absolute HTTP, HTTPS and mailto URIs are supported. Local files require a file-descriptor portal request.", nameof(uri));
        try
        {
            var response = await PortalRequest.RunAsync(owner, new PortalTransport(application: application), "OpenURI", uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            return response.Code == 0 ? new PlatformResult<Unit>.Success(new Unit()) : new PlatformResult<Unit>.Failed(FailureCode.IoError);
        }
        catch (OwnerClosedException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable); }
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

internal sealed class PortalFilePicker(IPortalWindowOwner owner, IPortalTransport transport, Action<PortalDiagnostic>? diagnosticSink = null) : INativeFilePicker
{
    public async ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken)
    {
        var result = await PortalRequest.RunAsync(owner, transport, save ? "SaveFile" : "OpenFile", suggestedName ?? "Untitled", cancellationToken, diagnosticSink).ConfigureAwait(false);
        if (result.Code == 1) return null;
        if (result.Code != 0) throw new NativeBackendUnavailableException();
        if (result.Uris.Length != 1 || !Uri.TryCreate(result.Uris[0], UriKind.Absolute, out var uri)
            || !uri.IsFile || !string.IsNullOrEmpty(uri.Host) && uri.Host != "localhost"
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.LocalPath.Contains('\0'))
            throw new IOException("The portal did not return one local file.");
        return new NativeFileSelection(uri.LocalPath, null, AllowsSiblingReplacement: false);
    }
}

internal interface IExplicitUnparentedOwner : IPortalWindowOwner;

internal static class PortalRequest
{
    internal static async ValueTask<PortalResponse> RunAsync(IPortalWindowOwner owner, IPortalTransport transport,
        string method, string argument, CancellationToken cancellationToken, Action<PortalDiagnostic>? diagnosticSink = null)
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
                var response = await transport.RequestAsync(parent.Identifier, method, argument, stopped.Token).ConfigureAwait(false);
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
