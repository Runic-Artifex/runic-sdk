using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using TUnit.Core;
using Tmds.DBus.Protocol;

internal sealed class WireProtocolTests
{
    [Test, Explicit, Category("DBus"), DisplayName("real D-Bus protocol: early response, legacy handle, save options, Request.Close")]
    public async Task RealDBusProtocolAsync()
    {
        var owner = new Owner();
        using var service = new DBusConnection(DBusAddress.Session!);
        await service.ConnectAsync();
        var fake = new PortalService(service);
        service.AddMethodHandler(fake);
        var realTransport = new PortalTransport(destination: service.UniqueName!);
        var directoryWireResult = await realTransport.RequestAsync("x11:1234", "SelectDirectory", "", default);
        Check(directoryWireResult.Code == 0 && fake.DirectorySelection, "directory option encoded as true");
        Check(fake.Call == "org.freedesktop.portal.FileChooser.OpenFile(ssa{sv}) handle_token,modal,directory" && fake.Argument == "Open directory",
            "directory picking uses FileChooser.OpenFile with directory option");
        fake.FileChooserVersion = 2;
        fake.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await realTransport.RequestAsync("x11:1234", "SelectDirectory", "", default); throw new InvalidOperationException("old portal accepted directory request"); }
        catch (NativeBackendUnavailableException) { }
        Check(!fake.Called.Task.IsCompleted, "old portal never receives a misleading file request");
        fake.FileChooserVersion = 3;
        foreach (bool oldHandle in new[] { false, true })
        {
            fake.OldHandle = oldHandle;
            fake.AutoRespond = true;
            var result = await realTransport.RequestAsync("x11:1234", "SaveFile", "saved.txt", default);
            Check(result.Code == 0 && result.Uris.Single() == "file:///tmp/saved.txt", "real D-Bus early response and returned handle");
            Check(fake.Parent == "x11:1234" && fake.CurrentName == "saved.txt", "parent and filename encoded");
            Check(fake.Call == "org.freedesktop.portal.FileChooser.SaveFile(ssa{sv}) handle_token,modal,current_name" && fake.Argument == "Save file",
                "SaveFile wire shape and option order");
        }
        fake.MalformedUris = true;
        try { await new PortalFilePicker(owner, realTransport).SelectAsync(true, "saved.txt", default); throw new InvalidOperationException("malformed portal URIs accepted"); }
        catch (IOException) { }
        fake.MalformedUris = false;
        var uriResult = await realTransport.RequestAsync("x11:1234", "OpenURI", "https://example.org/", default);
        Check(uriResult.Code == 0 && fake.Argument == "https://example.org/", "OpenURI request encoding");
        Check(fake.Call == "org.freedesktop.portal.OpenURI.OpenURI(ssa{sv}) handle_token", "OpenURI wire shape");
        fake.AutoRespond = false;
        fake.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancellation = new CancellationTokenSource())
        {
            var waiting = realTransport.RequestAsync("x11:1234", "OpenFile", "", cancellation.Token).AsTask();
            await fake.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("D-Bus cancellation ignored"); } catch (OperationCanceledException) { }
            await fake.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(fake.Call == "org.freedesktop.portal.FileChooser.OpenFile(ssa{sv}) handle_token,modal" && fake.Argument == "Open file", "OpenFile wire shape");
            Check(fake.ClosedPath == fake.RequestPath && fake.ClosedCall == "org.freedesktop.portal.Request.Close()", "Request.Close targets the returned handle");
        }
        fake.Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = realTransport.RequestAsync("x11:1234", "OpenFile", "", default).AsTask();
        await fake.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.Dispose();
        try { await disconnected.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("portal disconnection ignored"); }
        catch (DBusExceptionBase) { }
        catch (System.Threading.Channels.ChannelClosedException) { }
        Console.WriteLine("PASS real D-Bus protocol: early response, legacy handle, save options, Request.Close.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
