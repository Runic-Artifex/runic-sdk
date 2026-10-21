using System.Runtime.InteropServices;

// The pointer of the test display through XTEST: never the host's input.
internal sealed class Pointer : IDisposable
{
    private readonly nint _x11;
    private readonly nint _xtest;
    private readonly nint _display;

    private Pointer(nint x11, nint xtest, nint display) => (_x11, _xtest, _display) = (x11, xtest, display);

    internal static unsafe Pointer? Open()
    {
        if (!NativeLibrary.TryLoad("libX11.so.6", out var x11)
            && !NativeLibrary.TryLoad(Environment.GetEnvironmentVariable("RUNIC_LIBX11") ?? "libX11.so.6", out x11))
        {
            Console.WriteLine("File drop: libX11.so.6 could not be loaded.");
            return null;
        }
        if (!NativeLibrary.TryLoad(Environment.GetEnvironmentVariable("RUNIC_LIBXTST") ?? "libXtst.so.6", out var xtest)
            && !NativeLibrary.TryLoad("libXtst.so.6", out xtest))
        {
            Console.WriteLine("File drop: libXtst.so.6 could not be loaded.");
            return null;
        }
        var display = ((delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(x11, "XOpenDisplay"))(0);
        return display == 0 ? null : new Pointer(x11, xtest, display);
    }

    internal async Task DragAsync((int X, int Y) from, (int X, int Y) to)
    {
        Move(from.X, from.Y);
        await Task.Delay(150);
        Button(true);
        await Task.Delay(150);
        const int steps = 30;
        for (var step = 1; step <= steps; step++)
        {
            Move(from.X + (to.X - from.X) * step / steps, from.Y + (to.Y - from.Y) * step / steps);
            await Task.Delay(30);
        }
        // Let WebKit fetch the drag's data and the page see dragover before the drop.
        for (var wiggle = 0; wiggle < 10; wiggle++)
        {
            Move(to.X + wiggle % 2, to.Y);
            await Task.Delay(50);
        }
        // The last position must be answered before the drop.
        await Task.Delay(500);
        Button(false);
    }

    // A real primary-button click, which WebKit reports as a user gesture.
    internal async Task ClickAsync(int x, int y)
    {
        Move(x, y);
        await Task.Delay(150);
        Button(true);
        await Task.Delay(50);
        Button(false);
    }

    private unsafe void Move(int x, int y)
    {
        ((delegate* unmanaged[Cdecl]<nint, int, int, int, nuint, int>)NativeLibrary.GetExport(_xtest, "XTestFakeMotionEvent"))(_display, -1, x, y, 0);
        Flush();
    }

    private unsafe void Button(bool pressed)
    {
        ((delegate* unmanaged[Cdecl]<nint, uint, int, nuint, int>)NativeLibrary.GetExport(_xtest, "XTestFakeButtonEvent"))(_display, 1, pressed ? 1 : 0, 0);
        Flush();
    }

    private unsafe void Flush() => ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_x11, "XFlush"))(_display);

    public unsafe void Dispose() => ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_x11, "XCloseDisplay"))(_display);
}
