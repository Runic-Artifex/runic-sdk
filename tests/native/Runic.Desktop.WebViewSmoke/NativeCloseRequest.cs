using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Exercise the OS entry points rather than calling DesktopWindow.RequestCloseAsync alone.
internal static partial class NativeCloseRequest
{
    internal static unsafe Task SendAsync(nint window)
    {
        if (OperatingSystem.IsWindows())
        {
            if (PostMessage(window, 0x0010, 0, 0) == 0) throw new InvalidOperationException("WM_CLOSE could not be posted.");
            return Task.CompletedTask;
        }
        if (OperatingSystem.IsMacOS())
        {
            // performClose: consults windowShouldClose:; close bypasses that delegate.
            SendOnMainThread(window, Selector("performSelectorOnMainThread:withObject:waitUntilDone:"), Selector("performClose:"), 0, 0);
            return Task.CompletedTask;
        }
        var request = new GtkRequest(window);
        var handle = GCHandle.Alloc(request);
        if (AddIdle((nint)(delegate* unmanaged[Cdecl]<nint, int>)&CloseGtkWindow, GCHandle.ToIntPtr(handle)) == 0)
        {
            handle.Free();
            throw new InvalidOperationException("GTK close could not be scheduled.");
        }
        return request.Completion.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CloseGtkWindow(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (GtkRequest)handle.Target!;
        try
        {
            GtkWindowClose(request.Window);
            request.Completion.SetResult();
        }
        catch (Exception exception)
        {
            request.Completion.SetException(exception);
        }
        finally
        {
            handle.Free();
        }
        return 0;
    }

    private sealed record GtkRequest(nint Window)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    private static partial int PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_idle_add")]
    private static partial uint AddIdle(nint callback, nint context);

    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_close")]
    private static partial void GtkWindowClose(nint window);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void SendOnMainThread(nint receiver, nint selector, nint method, nint argument, byte wait);
}
