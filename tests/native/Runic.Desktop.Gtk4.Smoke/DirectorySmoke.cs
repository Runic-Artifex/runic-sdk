using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Linux.Gtk4;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;
using System.Runtime.Versioning;

[SupportedOSPlatform("linux")]
internal static class DirectorySmoke
{
    private static DBusConnection? _connection;
    internal static IDisposable StartPortal()
    {
        var connection = new DBusConnection(DBusAddress.Session!);
        try
        {
            connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
            if (!connection.TryRequestNameAsync("org.freedesktop.portal.Desktop", RequestNameOptions.None).GetAwaiter().GetResult())
                throw new InvalidOperationException("Run the directory smoke in an isolated dbus-run-session.");
            _connection = connection;
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    // Run under dbus-run-session. This controls only the portal service boundary;
    // the window, owner export, production provider and leases are real.
    internal static async Task RunAsync(INativePickerOwner owner, IDesktopWindowHost host)
    {
        var connection = _connection ?? throw new InvalidOperationException("Start the controlled portal before GTK initialization.");
        string root = Path.Combine(Path.GetTempPath(), "runic-gtk4-directory-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "space % Ω 文本\nline");
        Directory.CreateDirectory(path);
        try
        {
            var portal = new DirectoryPortal(connection, new Uri(path).AbsoluteUri);
            connection.AddMethodHandler(portal);
            var backend = PortalPlatformProvider.CreateFileDialogs(Gtk4PlatformProvider.CreatePortalWindowOwner(owner));
            await using var lifetime = new PresentationLifetime(() => owner.IsAvailable, owner.Generation);
            var files = new PresentationFiles(lifetime, backend);
            Check(files.GetSnapshot().Statuses["platform.directories.open"] is CapabilityStatus.Available, "directory capability");
            var selection = await files.OpenDirectoryAsync(new());
            Check(selection is PickerResult<IDirectoryLease>.Selected, "directory selection: " + selection);
            var selected = (PickerResult<IDirectoryLease>.Selected)selection;
            Check(selected.Value.LocalPath == path && Directory.Exists(selected.Value.LocalPath), "exact acquired directory access");
            Check(portal.Parent?.StartsWith("x11:", StringComparison.Ordinal) == true || portal.Parent?.StartsWith("wayland:", StringComparison.Ordinal) == true, "real GTK4 parent");
            Check(portal.Directory && portal.Title == "Open directory", "production FileChooser directory option");
            await selected.Value.DisposeAsync();

            portal.ResponseCode = 1;
            Check(await files.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Dismissed, "user dismissal");
            portal.ResponseCode = 0;
            portal.Hold = true;
            portal.Reset();
            using (var cancelled = new CancellationTokenSource())
            {
                var request = files.OpenDirectoryAsync(new(), cancelled.Token).AsTask();
                await portal.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancelled.Cancel();
                try { await request; throw new InvalidOperationException("Caller cancellation was ignored."); }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
                await portal.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            // An actual native owner disappearing stops the request even before
            // the presentation facade begins shutdown.
            portal.Reset();
            var closingRequest = files.OpenDirectoryAsync(new()).AsTask();
            await portal.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.CloseAsync();
            Check(await closingRequest.WaitAsync(TimeSpan.FromSeconds(5)) is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "native owner closure");
            await portal.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await lifetime.DisposeAsync();
            Check(await files.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "directory shutdown drained");
            Console.WriteLine("PASS GTK4 directory access: production portal wire request, exact local path, dismissal, cancellation, native owner close and drain (controlled portal service).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool value, string scenario) { if (!value) throw new InvalidOperationException("Directory smoke failed: " + scenario); }

    private sealed class DirectoryPortal(DBusConnection connection, string uri) : IPathMethodHandler
    {
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        internal bool Hold, Directory;
        internal uint ResponseCode;
        internal string? Parent, Title;
        internal TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Reset()
        { Called = new(TaskCreationOptions.RunContinuationsAsynchronously); Closed = new(TaskCreationOptions.RunContinuationsAsynchronously); }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.MemberAsString == "Get")
            {
                using var property = context.CreateReplyWriter("v");
                property.WriteVariant(VariantValue.UInt32(3));
                context.Reply(property.CreateMessage());
                return ValueTask.CompletedTask;
            }
            if (context.Request.MemberAsString == "Close")
            {
                using var close = context.CreateReplyWriter(null);
                context.Reply(close.CreateMessage());
                Closed.TrySetResult();
                return ValueTask.CompletedTask;
            }
            if (context.Request.InterfaceAsString != "org.freedesktop.portal.FileChooser" || context.Request.MemberAsString != "OpenFile")
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "This test service implements FileChooser only.");
                return ValueTask.CompletedTask;
            }
            var reader = context.Request.GetBodyReader();
            Parent = reader.ReadString();
            Title = reader.ReadString();
            string token = "";
            var options = reader.ReadDictionaryStart();
            while (reader.HasNext(options))
            {
                string key = reader.ReadString();
                var value = reader.ReadVariantValue();
                if (key == "handle_token") token = value.GetString();
                if (key == "directory") Directory = value.GetBool();
            }
            string requestPath = Path + "/request/" + context.Request.SenderAsString![1..].Replace('.', '_') + "/" + token;
            using var reply = context.CreateReplyWriter("o");
            reply.WriteObjectPath(new ObjectPath(requestPath));
            context.Reply(reply.CreateMessage());
            if (!Hold)
            {
                using var response = connection.GetMessageWriter();
                response.WriteSignalHeader(path: requestPath, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
                response.WriteUInt32(ResponseCode);
                var results = response.WriteDictionaryStart();
                response.WriteDictionaryEntryStart();
                response.WriteString("uris");
                response.WriteVariant(VariantValue.Array(new[] { uri }));
                response.WriteDictionaryEnd(results);
                connection.TrySendMessage(response.CreateMessage());
            }
            Called.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
