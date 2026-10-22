using System.Threading.Channels;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

internal sealed class PortalInhibition(IPortalWindowOwner owner, PortalApplication application,
    string? address = null, string destination = "org.freedesktop.portal.Desktop") : IDesktopInhibition
{
    public DesktopInhibitionEffects SupportedEffects => DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep;
    public async ValueTask<PlatformResult<IDesktopInhibitionLease>> AcquireAsync(DesktopInhibitionEffects effects, string reason, CancellationToken cancellationToken = default)
    {
        InhibitionValidation.Validate(effects, reason);
        cancellationToken.ThrowIfCancellationRequested();
        var generation = owner.Generation;
        PortalParentLease? parent = null;
        PortalConnection? session = null;
        // A request the portal may hold once its call was sent; closed again unless it becomes the lease.
        string? request = null;
        Task<DBusObjectPath>? inhibit = null;
        try
        {
            if (!owner.IsAvailable) return new PlatformResult<IDesktopInhibitionLease>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            parent = await owner.ExportParentAsync(cancellationToken).ConfigureAwait(false);
            // An explicitly unparented request sends the empty window identifier, which the Inhibit portal accepts.
            if (owner is not IExplicitUnparentedOwner
                && !(parent.Identifier.StartsWith("x11:", StringComparison.Ordinal) && parent.Identifier.Length > 4
                || parent.Identifier.StartsWith("wayland:", StringComparison.Ordinal) && parent.Identifier.Length > 8))
                throw new NativeBackendUnavailableException();
            if (!owner.IsAvailable || owner.Generation != generation) throw new OwnerClosedException();
            session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, parent.OwnerClosed, session.OwnerChanged);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await using var ownerMonitor = new Timer(_ =>
            {
                if (!owner.IsAvailable || owner.Generation != generation) deadline.Cancel();
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
            var connection = session.Connection;
            var prefix = "/org/freedesktop/portal/desktop/request/" + connection.UniqueName.TrimStart(':').Replace('.', '_') + "/";
            var responses = Channel.CreateUnbounded<(string Path, uint Code)>();
            await using var subscription = await connection.SubscribeAsync(new DBusSignalFilter
            {
                Sender = session.Destination, PathNamespace = new DBusObjectPath(prefix.TrimEnd('/')),
                Interface = "org.freedesktop.portal.Request", Member = "Response",
            }, deadline.Token).ConfigureAwait(false);
            _ = PortalValues.PumpAsync(subscription, signal =>
            {
                if (signal.Arguments is [{ Signature.Value: "u" } code, ..]) responses.Writer.TryWrite((signal.Path.Value, code.GetUInt32()));
            }, error => responses.Writer.TryComplete(error));
            string token = "runic_" + Guid.NewGuid().ToString("N");
            var options = new PortalOptions { ["handle_token"] = DBusValue.From(token), ["reason"] = DBusValue.From(reason) };
            uint flags = (effects.HasFlag(DesktopInhibitionEffects.SystemSleep) ? 4u : 0) | (effects.HasFlag(DesktopInhibitionEffects.DisplaySleep) ? 8u : 0);
            request = prefix + token;
            inhibit = PortalOptions.RequestAsync(new Protocol.Inhibit(connection, session.Destination, "/org/freedesktop/portal/desktop"),
                Protocol.Inhibit.InterfaceName, "Inhibit", [DBusValue.From(parent.Identifier), DBusValue.From(flags), options.ToValue()]);
            string handle = (await inhibit.WaitAsync(deadline.Token).ConfigureAwait(false)).Value;
            if (!handle.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Invalid inhibition request handle.");
            request = handle;
            while (true)
            {
                var response = await responses.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (response.Path != handle) continue;
                // The portal ends a request that it answered with an error; a granted one stays until it is closed.
                if (response.Code != 0) request = null;
                if (response.Code != 0)
                    return new PlatformResult<IDesktopInhibitionLease>.Failed(response.Code == 1 ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.Other,
                        PortalErrors.Response(response.Code));
                break;
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (!owner.IsAvailable || owner.Generation != generation) return new PlatformResult<IDesktopInhibitionLease>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            // The parent identifies the window only while the portal handles the request.
            // Release it now: GTK3 allows one Wayland export per window at a time.
            var lease = new Lease(session, handle, effects);
            request = null;
            return new PlatformResult<IDesktopInhibitionLease>.Success(lease);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new PlatformResult<IDesktopInhibitionLease>.Unavailable(parent?.OwnerClosed.IsCancellationRequested == true || !owner.IsAvailable || owner.Generation != generation ? PlatformUnavailableReason.OwnerClosed : PlatformUnavailableReason.BackendUnavailable); }
        catch (OwnerClosedException) { return new PlatformResult<IDesktopInhibitionLease>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (DBusException error) when (error.ErrorName is "org.freedesktop.portal.Error.NotAllowed" or "org.freedesktop.DBus.Error.AccessDenied")
        { return new PlatformResult<IDesktopInhibitionLease>.Failed(PlatformFailureCode.PermissionDenied, PortalErrors.Reply(error)); }
        catch (Exception error) when (error is DBusException or TimeoutException or NativeBackendUnavailableException or IOException or ChannelClosedException)
        { return new PlatformResult<IDesktopInhibitionLease>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        finally
        {
            // A failed or cancelled request is closed, even one whose handle reply raced cancellation.
            if (request is not null && session is not null) _ = PortalRequests.CloseAbandonedAsync(session, request, inhibit);
            if (parent is not null) await parent.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Lease(PortalConnection session, string handle, DesktopInhibitionEffects effects) : IDesktopInhibitionLease
    {
        private readonly object _gate = new();
        private Task? _dispose;
        public DesktopInhibitionEffects Effects => effects;
        public ValueTask DisposeAsync() { lock (_gate) return new(_dispose ??= PortalRequests.CloseAsync(session, handle)); }
    }
}
