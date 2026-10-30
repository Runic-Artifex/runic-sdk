using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Wpf;
using Runic.Desktop;
using Runic.Platform;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TUnit.Core;

// B11 (I-026): a ViewModel of a WPF window drags files out of the window's child web view through
// WpfPlatformServices.ForWindow. SendInput presses the button on a draggable element, the page cancels its own drag,
// and while the button is held the services start an OLE drag that drops onto a WPF drop target window. The services
// report the capability and why they cannot drag before the window opens and after it closed.
internal sealed partial class DragOutChecks
{
    private const string Page =
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic WPF drag out</title>
        <style>html,body{margin:0;height:100%} #item{position:fixed;left:40px;top:40px;width:160px;height:120px;background:#cde}</style></head>
        <body><div id="item" draggable="true">drag me</div>
        <script>
        window.__dragStarts = 0;
        document.getElementById("item").addEventListener("dragstart", event => { event.preventDefault(); window.__dragStarts++; });
        </script></body></html>
        """;

    [Test, NotInParallel("WpfSuite", Order = 32)]
    public async Task ForWindowDragsOutOfTheChildWebView()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"runic-wpf-drag-{Guid.NewGuid():N}.txt");
        await System.IO.File.WriteAllTextAsync(file, "dragged");
        using var child = new RunicWebView();
        var window = new Window { Content = child, Width = 480, Height = 360, Left = 100, Top = 100, Title = "Runic WPF drag out" };
        var services = WpfPlatformServices.ForWindow(window);
        var data = new DesktopDragData { Paths = [file] };
        Program.Check(await services.StartDragAsync(data) is PlatformResult<DesktopDragEffects>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "A window without a handle did not report the owner unavailable for a drag.");
        var dropped = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new Window
        {
            Width = 200, Height = 200, Left = 700, Top = 100, AllowDrop = true, ShowActivated = false, Title = "Runic WPF drop target",
            Content = new Border { Background = Brushes.LightGray },
        };
        target.DragOver += (_, args) => { args.Effects = DragDropEffects.Copy; args.Handled = true; };
        target.Drop += (_, args) =>
        {
            dropped.TrySetResult(args.Data.GetData(DataFormats.FileDrop) as string[] ?? []);
            args.Effects = DragDropEffects.Copy;
            args.Handled = true;
        };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Loaded += (_, _) => loaded.TrySetResult();
        window.Show();
        target.Show();
        try
        {
            await loaded.Task.WaitScaledAsync(TimeSpan.FromSeconds(10));
            Program.Check(services.GetSnapshot().Statuses[PlatformCapabilityIds.DragOut] is CapabilityStatus.Available,
                "The open WPF window's services did not report drags.");
            Program.Check(await Task.Run(async () => await services.StartDragAsync(data)) is PlatformResult<DesktopDragEffects>.Success(DesktopDragEffects.None),
                "A drag without a held button did not end at once with no effect.");
            var collection = new ServiceCollection();
            collection.AddSingleton<Func<IBridgeTransport, object, IDisposable>>((_, _) => new Attachment());
            await using var provider = collection.BuildServiceProvider();
            await using var host = await DesktopHost.StartAsync(new()
            {
                WindowHostFactory = child.CreateWindowHostFactory(),
                ConnectionTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(20)),
            });
            await using var view = await provider.CreateWpfViewAsync(host, new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }, new object());
            await view.OpenAsync();
            await view.Surface.ExecuteJavaScriptAsync("return document.readyState;", TimeSpan.FromSeconds(15));
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                Console.WriteLine("WPF drag out: the OLE drag is unverified on Windows arm64, whose runner does not run the OLE drag loop (I-077).");
                return;
            }
            window.Activate();
            var press = child.PointToScreen(new System.Windows.Point(120, 100));
            var drop = target.PointToScreen(new System.Windows.Point(100, 100));
            var (x, y) = ((int)press.X, (int)press.Y);
            Mouse(x, y, MouseMove);
            await Task.Delay(300);
            Mouse(x, y, MouseLeftDown);
            await Task.Delay(200);
            if (GetAsyncKeyState(0x01) >= 0)
            {
                Mouse(x, y, MouseLeftUp);
                Console.WriteLine("WPF drag out: unverified on this runner: its desktop does not deliver SendInput to a window of this process.");
                return;
            }
            for (var step = 1; step <= 10; step++)
            {
                Mouse(x + step * 2, y + step, MouseMove);
                await Task.Delay(30);
            }
            var started = false;
            for (var attempt = 0; attempt < 50 && !started; attempt++)
            {
                started = await view.Surface.ExecuteJavaScriptAsync("return String(window.__dragStarts);") != "0";
                if (!started) await Task.Delay(50);
            }
            if (!started)
            {
                Mouse(x, y, MouseLeftUp);
                throw new InvalidOperationException("The child web view's page saw no dragstart.");
            }
            var drag = services.StartDragAsync(data).AsTask();
            var mover = Task.Run(async () =>
            {
                await Task.Delay(400);
                var from = (X: x + 20, Y: y + 10);
                var to = ((int)drop.X, (int)drop.Y);
                for (var step = 1; step <= 30; step++)
                {
                    Mouse(from.X + (to.Item1 - from.X) * step / 30, from.Y + (to.Item2 - from.Y) * step / 30, MouseMove);
                    await Task.Delay(30);
                }
                for (var wiggle = 0; wiggle < 10; wiggle++)
                {
                    Mouse(to.Item1 + wiggle % 2, to.Item2, MouseMove);
                    await Task.Delay(50);
                }
                await Task.Delay(300);
                Mouse(to.Item1, to.Item2, MouseLeftUp);
            });
            PlatformResult<DesktopDragEffects> result;
            try { result = await drag.WaitScaledAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                Mouse((int)drop.X, (int)drop.Y, MouseLeftUp);
                throw new TimeoutException($"The WPF drag out did not end within {TestTimeouts.Scale(TimeSpan.FromSeconds(30)).TotalSeconds:0} seconds.");
            }
            await mover;
            var paths = await dropped.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
            Program.Check(result is PlatformResult<DesktopDragEffects>.Success(DesktopDragEffects.Copy)
                && paths.SequenceEqual([file], StringComparer.OrdinalIgnoreCase),
                $"The WPF drag out ended with {result} and dropped [{string.Join(", ", paths)}].");
            Console.WriteLine("WPF drag out passed: the child web view's dragstart started an OLE drag that dropped the file with Copy.");
        }
        finally
        {
            target.Close();
            window.Close();
        }
        // The data rules run first, so the file still exists here.
        Program.Check(await services.StartDragAsync(data) is PlatformResult<DesktopDragEffects>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "A closed window did not report the owner closed for a drag.");
        System.IO.File.Delete(file);
    }

    private sealed class Attachment : IDisposable
    {
        public void Dispose() { }
    }

    private const uint MouseMove = 0x0001 | 0x8000; // MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
    private const uint MouseLeftDown = 0x0002 | 0x8000;
    private const uint MouseLeftUp = 0x0004 | 0x8000;

    private static unsafe void Mouse(int x, int y, uint flags)
    {
        // Absolute input spans the virtual desktop with MOUSEEVENTF_VIRTUALDESK.
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = Math.Max(1, GetSystemMetrics(78) - 1);
        var height = Math.Max(1, GetSystemMetrics(79) - 1);
        var input = new Input
        {
            Type = 0,
            Mouse = new MouseInput { X = (int)((x - left) * 65535L / width), Y = (int)((y - top) * 65535L / height), Flags = flags | 0x4000 },
        };
        _ = SendInput(1, &input, sizeof(Input));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint Data;
        internal uint Flags;
        internal uint Time;
        internal nuint Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal MouseInput Mouse;
    }

    [LibraryImport("user32.dll", SetLastError = true)] private static unsafe partial uint SendInput(uint count, Input* inputs, int size);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int key);
}
