using Runic.Platform.Runtime;
using System.Threading.Channels;
using Tmds.DBus.Protocol;

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
        try
        {
            if (!owner.IsAvailable) return new PlatformResult<IDesktopInhibitionLease>.Unavailable(UnavailableReason.OwnerClosed);
            parent = await owner.ExportParentAsync(cancellationToken).ConfigureAwait(false);
            if (!(parent.Identifier.StartsWith("x11:", StringComparison.Ordinal) && parent.Identifier.Length > 4
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
            var prefix = "/org/freedesktop/portal/desktop/request/" + connection.UniqueName!.TrimStart(':').Replace('.', '_') + "/";
            var responses = Channel.CreateUnbounded<(string Path, uint Code)>();
            using var subscription = await connection.AddMatchAsync(new MatchRule
            {
                Type = MessageType.Signal, Sender = session.Destination, PathNamespace = prefix.TrimEnd('/'),
                Interface = "org.freedesktop.portal.Request", Member = "Response",
            }, static (message, _) => (message.PathAsString!, message.GetBodyReader().ReadUInt32()), value =>
            {
                if (value.HasValue) responses.Writer.TryWrite(value.Value);
                else if (value.IsCompletion) responses.Writer.TryComplete(value.Exception);
            }, emitOnCapturedContext: false, flags: ObserverFlags.EmitAll).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            var options = new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String("runic_" + Guid.NewGuid().ToString("N")),
                ["reason"] = VariantValue.String(reason),
            };
            uint flags = (effects.HasFlag(DesktopInhibitionEffects.SystemSleep) ? 4u : 0) | (effects.HasFlag(DesktopInhibitionEffects.DisplaySleep) ? 8u : 0);
            string handle = (await new Protocol.Inhibit(connection, session.Destination, "/org/freedesktop/portal/desktop")
                .InhibitAsync(parent.Identifier, flags, options).WaitAsync(deadline.Token).ConfigureAwait(false)).ToString();
            if (!handle.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Invalid inhibition request handle.");
            while (true)
            {
                var response = await responses.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (response.Path != handle) continue;
                if (response.Code != 0) return new PlatformResult<IDesktopInhibitionLease>.Failed(response.Code == 1 ? FailureCode.PermissionDenied : FailureCode.IoError);
                break;
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (!owner.IsAvailable || owner.Generation != generation) return new PlatformResult<IDesktopInhibitionLease>.Unavailable(UnavailableReason.OwnerClosed);
            var lease = new Lease(session, parent, handle, effects);
            session = null; parent = null;
            return new PlatformResult<IDesktopInhibitionLease>.Success(lease);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new PlatformResult<IDesktopInhibitionLease>.Unavailable(parent?.OwnerClosed.IsCancellationRequested == true || !owner.IsAvailable || owner.Generation != generation ? UnavailableReason.OwnerClosed : UnavailableReason.BackendUnavailable); }
        catch (OwnerClosedException) { return new PlatformResult<IDesktopInhibitionLease>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (DBusErrorReplyException error) when (error.ErrorName is "org.freedesktop.portal.Error.NotAllowed" or "org.freedesktop.DBus.Error.AccessDenied")
        { return new PlatformResult<IDesktopInhibitionLease>.Failed(FailureCode.PermissionDenied); }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or NativeBackendUnavailableException or IOException or ChannelClosedException)
        { return new PlatformResult<IDesktopInhibitionLease>.Unavailable(UnavailableReason.BackendUnavailable); }
        finally
        {
            // Failed/cancelled calls lose their dedicated bus connection, which
            // releases even a request whose handle reply raced cancellation.
            session?.Dispose();
            if (parent is not null) await parent.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Lease(PortalConnection session, PortalParentLease parent, string handle, DesktopInhibitionEffects effects) : IDesktopInhibitionLease
    {
        private readonly object _gate = new();
        private Task? _dispose;
        public DesktopInhibitionEffects Effects => effects;
        public ValueTask DisposeAsync() { lock (_gate) return new(_dispose ??= CloseAsync()); }
        private MessageBuffer CloseMessage()
        {
            using var writer = session.Connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: session.Destination, path: handle, @interface: "org.freedesktop.portal.Request", member: "Close");
            return writer.CreateMessage();
        }
        private async Task CloseAsync()
        {
            try
            {
                if (!session.OwnerChanged.IsCancellationRequested)
                {
                    await session.Connection.CallMethodAsync(CloseMessage()).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is DBusExceptionBase or TimeoutException) { /* Disconnect also releases the request. */ }
            finally { session.Dispose(); await parent.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
