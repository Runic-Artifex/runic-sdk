using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Tmds.DBus.Protocol;

internal static class InhibitionTests
{
    internal static async Task RunAsync()
    {
        using var bus = new DBusConnection(DBusAddress.Session!);
        await bus.ConnectAsync();
        var service = new Service(bus);
        bus.AddMethodHandler(service);
        var owner = new Owner();
        var provider = new PortalInhibition(owner, new PortalApplication(), destination: bus.UniqueName!);
        foreach (bool legacy in new[] { false, true })
        {
            service.Legacy = legacy;
            int before = owner.Released;
            var result = await provider.AcquireAsync(DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep, "Export documents");
            if (result is not PlatformResult<IDesktopInhibitionLease>.Success success || service.Flags != 12 || service.Reason != "Export documents" || owner.Released != before)
                throw new InvalidOperationException("Inhibition did not retain an owned parent and accepted request.");
            var second = (PlatformResult<IDesktopInhibitionLease>.Success)await provider.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "Independent operation");
            int closes = service.Closes;
            await Task.WhenAll(success.Value.DisposeAsync().AsTask(), success.Value.DisposeAsync().AsTask());
            if (service.Closes != closes + 1 || owner.Released != before + 1) throw new InvalidOperationException("Inhibition release was not idempotent and independent.");
            await second.Value.DisposeAsync();
            if (service.Closes != closes + 2) throw new InvalidOperationException("Second lease lost independent ownership.");
        }
        service.Deny = true;
        if (await provider.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "Denied") is not PlatformResult<IDesktopInhibitionLease>.Failed { Code: FailureCode.PermissionDenied })
            throw new InvalidOperationException("Inhibition denial not preserved.");
        service.Deny = false;
        service.Respond = false;
        using var cancellation = new CancellationTokenSource();
        service.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = provider.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "Cancelled", cancellation.Token).AsTask();
        await service.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        try { await pending; throw new InvalidOperationException("Cancelled inhibition succeeded."); }
        catch (OperationCanceledException) { }
        service.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacedOwner = provider.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "Replaced window").AsTask();
        await service.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));
        owner.Generation = Guid.NewGuid();
        if (await replacedOwner.WaitAsync(TimeSpan.FromSeconds(3)) is not PlatformResult<IDesktopInhibitionLease>.Unavailable { Reason: UnavailableReason.OwnerClosed })
            throw new InvalidOperationException("A replaced owner did not cancel pending inhibition.");
        owner.IsAvailable = false;
        if (await provider.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "Closed") is not PlatformResult<IDesktopInhibitionLease>.Unavailable { Reason: UnavailableReason.OwnerClosed })
            throw new InvalidOperationException("Closed inhibition owner was accepted.");
        owner.IsAvailable = true;
        service.Respond = true;
        const string name = "org.runic.InhibitionPortal";
        if (!await bus.TryRequestNameAsync(name, RequestNameOptions.AllowReplacement)) throw new InvalidOperationException("Cannot acquire test portal name.");
        var namedProvider = new PortalInhibition(owner, new PortalApplication(), destination: name);
        var held = (PlatformResult<IDesktopInhibitionLease>.Success)await namedProvider.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "Before restart");
        service.Respond = false;
        service.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarting = namedProvider.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "During restart").AsTask();
        await service.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var replacementBus = new DBusConnection(DBusAddress.Session!);
        await replacementBus.ConnectAsync();
        var replacement = new Service(replacementBus);
        replacementBus.AddMethodHandler(replacement);
        if (!await replacementBus.TryRequestNameAsync(name, RequestNameOptions.ReplaceExisting)) throw new InvalidOperationException("Cannot replace test portal.");
        if (await restarting.WaitAsync(TimeSpan.FromSeconds(3)) is not PlatformResult<IDesktopInhibitionLease>.Unavailable { Reason: UnavailableReason.BackendUnavailable })
            throw new InvalidOperationException("Portal restart did not invalidate pending acquisition.");
        await held.Value.DisposeAsync();
        if (replacement.Called.Task.IsCompleted || replacement.Closes != 0) throw new InvalidOperationException("Old inhibition was replayed or closed on a replacement portal.");
        await using var recovered = ((PlatformResult<IDesktopInhibitionLease>.Success)await namedProvider.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "After restart")).Value;
        Console.WriteLine("PASS inhibition: early/legacy replies, flags/reason, parent retention, independent/idempotent release, denial, cancellation, owner replacement and portal restart without replay.");
    }

    private sealed class Service(DBusConnection connection) : IPathMethodHandler
    {
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        internal uint Flags;
        internal string? Reason;
        internal int Closes;
        internal bool Legacy;
        internal bool Deny;
        internal bool Respond = true;
        private int _number;
        internal TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.MemberAsString == "Close")
            {
                Closes++;
                using var response = context.CreateReplyWriter(null); context.Reply(response.CreateMessage());
                return ValueTask.CompletedTask;
            }
            if (Deny) { context.ReplyError("org.freedesktop.portal.Error.NotAllowed", "Denied"); return ValueTask.CompletedTask; }
            var reader = context.Request.GetBodyReader();
            if (reader.ReadString() != "x11:1234") throw new InvalidOperationException("Owner not forwarded.");
            Flags = reader.ReadUInt32();
            var options = reader.ReadDictionaryOfStringToVariantValue();
            Reason = options["reason"].GetString();
            string path = Path + "/request/" + context.Request.SenderAsString![1..].Replace('.', '_') + "/" + (Legacy ? "legacy" + ++_number : options["handle_token"].GetString());
            if (Respond)
            {
                using var signal = connection.GetMessageWriter();
                signal.WriteSignalHeader(path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
                signal.WriteUInt32(0); var dict = signal.WriteDictionaryStart(); signal.WriteDictionaryEnd(dict);
                connection.TrySendMessage(signal.CreateMessage());
            }
            using var reply = context.CreateReplyWriter("o"); reply.WriteObjectPath(new ObjectPath(path)); context.Reply(reply.CreateMessage());
            Called.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
