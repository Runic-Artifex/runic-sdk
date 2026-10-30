using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Runic.Desktop;
using Runic.Platform;

// B11 (I-026) on GTK 3 and GTK 4, under Xvfb with XTEST: a drag out of the page window and drops into frames. XTEST
// presses the button on a draggable element, the page's dragstart cancels the HTML5 drag and counts it, and while the
// button is still held (WebKit holding the implicit grab) the smoke starts the window's drag, moves onto a drop target
// window of this process that records the dropped URIs, and releases there; then it cancels a drag with Escape (GTK 3)
// or drops it where nothing accepts it (GTK 4, which cancels X11 drags only so). A call without a held button ends at
// once with None. Then a GTK drag source drops files into a same-origin frame, which delivers its label and
// effect, and into a data: frame, which delivers nothing. Shared by the GTK 3 and GTK 4 smokes, which supply their
// toolkit's test windows.
internal static class LinuxDragOutSmoke
{
    internal const string DragOutPage =
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic drag out</title>
        <style>html,body{margin:0;height:100%} #item{position:fixed;inset:0;background:#cde}</style></head>
        <body><div id="item" draggable="true">drag me</div>
        <script>
        window.__dragStarts = 0;
        // The page cancels its own drag and asks the application for a native one, here through the smoke's polling.
        document.getElementById("item").addEventListener("dragstart", event => { event.preventDefault(); window.__dragStarts++; });
        </script></body></html>
        """;

    internal const string FramePage =
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic frame drops</title>
        <style>html,body{margin:0;height:100%} iframe{position:fixed;top:0;width:50%;height:100%;border:0}</style></head>
        <body><iframe id="same" src="frame.html" style="left:0"></iframe>
        <iframe id="other" src="data:text/html,%3Cbody%20style%3D%22margin%3A0%3Bheight%3A100vh%3Bbackground%3A%23edc%22%20data-runic-drop-target%3D%22other%22%3E%3C%2Fbody%3E" style="left:50%"></iframe>
        </body></html>
        """;

    internal const string FrameDocument =
        """
        <!doctype html><html><head><meta charset="utf-8"><title>Same-origin frame</title></head>
        <body style="margin:0;height:100vh;background:#cde" data-runic-drop-target="frame-folder" data-runic-drop-effect="move">
        <script>window.__drops = 0; addEventListener("drop", () => window.__drops++);</script></body></html>
        """;

    private static TaskCompletionSource<string[]> s_dropped = NewDrop();
    // I-214: the stages of the running drag and what its drop target saw, with their times, for a failure's message.
    private static readonly Stopwatch s_clock = new();
    private static readonly ConcurrentQueue<string> s_stages = new();

    // The toolkit's drop target window reports the URIs of a drop here (GTK thread).
    internal static void RecordDrop(string[] uris)
    {
        Stage($"the drop target received {uris.Length} URIs");
        s_dropped.TrySetResult(uris);
    }

    // A stage of the running drag, or what the toolkit's drop target saw (any thread).
    internal static void Stage(string stage) => s_stages.Enqueue($"{s_clock.ElapsedMilliseconds} ms {stage}");

    private static string Stages() => string.Join("; ", s_stages);

    // The toolkit's test windows and where things are on the test screen. The page window opens with PageWindow and
    // its web view covers PageCenter; the Create functions run on the GTK thread.
    internal sealed class Toolkit
    {
        internal required string Name { get; init; }
        internal required DesktopWindowOptions PageWindow { get; init; }
        internal required (int X, int Y) PageCenter { get; init; }
        // Points in the left (same-origin) and right (data:) frame of the frame page.
        internal required (int X, int Y) SameFrame { get; init; }
        internal required (int X, int Y) OtherFrame { get; init; }
        internal required Func<nint> CreateDropTarget { get; init; }
        internal required (int X, int Y) DropPoint { get; init; }
        // GTK 4's X11 drag grabs only the pointer and ignores Escape (gdkdrag-x11.c); its cancelled drag is released
        // over this point, where no window accepts drops.
        internal (int X, int Y)? NowherePoint { get; init; }
        // A drag source offering text/uri-list with Copy and Move.
        internal required Func<string[], nint> CreateDragSource { get; init; }
        internal required (int X, int Y) SourcePoint { get; init; }
        internal required Action<nint> DestroyWindow { get; init; }
        // Closes the page window as its title bar's close button does; without it the window is disposed. GTK 4 closes
        // it so, after the drags (I-197).
        internal Func<DesktopWindow, Task>? CloseNatively { get; init; }
    }

    internal static async Task RunAsync(DesktopHost host, Pointer pointer, string[] paths, Toolkit toolkit)
    {
        await DragOutAsync(host, pointer, paths, toolkit).WaitScaledAsync(TimeSpan.FromSeconds(45));
        Console.WriteLine($"{toolkit.Name} drag out: the window closed.");
        await FrameDropsAsync(host, pointer, paths, toolkit).WaitScaledAsync(TimeSpan.FromSeconds(45));
    }

    private static async Task DragOutAsync(DesktopHost host, Pointer pointer, string[] paths, Toolkit toolkit)
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(DragOutPage) });
        await using var window = await surface.OpenWindowAsync(toolkit.PageWindow);
        if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.FileDrag))
            throw new InvalidOperationException($"The {toolkit.Name} window did not advertise drags out.");
        await surface.ExecuteJavaScriptAsync("return document.readyState;", TimeSpan.FromSeconds(10));
        // Without a held button the drag does not start.
        if (await window.StartDragAsync(new DesktopDragData { Text = "late" }) != DesktopDragEffects.None)
            throw new InvalidOperationException($"A {toolkit.Name} drag without a held button did not end at once with no effect.");
        nint target = 0;
        await window.InvokeNativeAsync(_ => target = toolkit.CreateDropTarget());
        try
        {
            await Task.Delay(700);
            var data = new DesktopDragData { Paths = [.. paths], AllowedEffects = DesktopDragEffects.Copy | DesktopDragEffects.Move };
            var (effect, uris) = await DragOnceAsync(surface, window, pointer, toolkit, data, escape: false);
            var dropped = uris?.Select(static uri => new Uri(uri).LocalPath).ToArray() ?? [];
            if (effect != DesktopDragEffects.Copy || !dropped.SequenceEqual(paths))
                throw new InvalidOperationException($"The {toolkit.Name} drag out ended with {effect} and dropped [{string.Join(", ", dropped)}].");
            Console.WriteLine($"{toolkit.Name} drag out: dropped with {effect}: [{string.Join(", ", uris!)}].");

            var (cancelled, cancelledUris) = await DragOnceAsync(surface, window, pointer, toolkit, data, escape: true);
            var how = toolkit.NowherePoint is null ? "Escape" : "a drop where nothing accepts it";
            if (cancelled != DesktopDragEffects.None || cancelledUris is not null)
                throw new InvalidOperationException($"The {toolkit.Name} drag out cancelled with {how} ended with {cancelled}.");
            Console.WriteLine($"{toolkit.Name} drag out passed: files dropped with Copy, {how} ended with None, a late call with None.");
        }
        finally
        {
            Console.WriteLine($"{toolkit.Name} drag out: destroying the drop target.");
            await window.InvokeNativeAsync(_ => toolkit.DestroyWindow(target));
        }
        if (toolkit.CloseNatively is { } close)
        {
            Console.WriteLine($"{toolkit.Name} drag out: closing the window natively.");
            await close(window);
            // Completes once the window and its web view were released.
            await window.WaitForCloseAsync();
        }
        else
        {
            Console.WriteLine($"{toolkit.Name} drag out: closing the window.");
        }
    }

    // One drag: press on the element, move until the page's dragstart, start the window's drag while the button is
    // held, then move onto the drop target and release, or cancel: press Escape there, or release over the toolkit's
    // nowhere point. The drop target's URIs, or null.
    private static async Task<(DesktopDragEffects Effect, string[]? Uris)> DragOnceAsync(DesktopSurface surface, DesktopWindow window,
        Pointer pointer, Toolkit toolkit, DesktopDragData data, bool escape)
    {
        s_dropped = NewDrop();
        s_stages.Clear();
        s_clock.Restart();
        var startsBefore = await surface.ExecuteJavaScriptAsync("return String(window.__dragStarts);");
        var press = toolkit.PageCenter;
        await pointer.PressAsync(press.X, press.Y);
        await Task.Delay(150);
        // WebKit starts the HTML5 drag after a few pixels of movement with the button held.
        var moved = (X: press.X + 20, Y: press.Y + 10);
        await pointer.MoveAsync(press, moved, steps: 10);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await surface.ExecuteJavaScriptAsync("return String(window.__dragStarts);") == startsBefore)
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(10))
            {
                pointer.Release();
                throw new TimeoutException($"The {toolkit.Name} page saw no dragstart. Stages: {Stages()}");
            }
            await Task.Delay(50);
        }
        Stage("the page saw dragstart");
        var drag = window.StartDragAsync(data).AsTask();
        await Task.Delay(300);
        if (drag.IsCompleted)
        {
            pointer.Release();
            throw new InvalidOperationException($"The {toolkit.Name} drag out ended at once with {await drag} while the button was held. Stages: {Stages()}");
        }
        _ = drag.ContinueWith(static (ended, _) => Stage($"the drag ended with {(ended.IsCompletedSuccessfully ? ended.Result : ended.Status)}"),
            null, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var end = escape && toolkit.NowherePoint is { } nowhere ? nowhere : toolkit.DropPoint;
        await pointer.MoveAsync(moved, end);
        Stage($"the pointer reached ({end.X}, {end.Y})");
        // Let the target answer the last position before the drop.
        for (var wiggle = 0; wiggle < 6; wiggle++)
        {
            await pointer.MoveAsync(end, (end.X + wiggle % 2, end.Y), steps: 1);
            await Task.Delay(50);
        }
        await Task.Delay(500);
        if (escape && toolkit.NowherePoint is null)
        {
            Stage("Escape");
            pointer.Escape();
            await Task.Delay(300);
        }
        Stage("the button was released");
        pointer.Release();
        DesktopDragEffects effect;
        try
        {
            effect = await drag.WaitScaledAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            pointer.Escape();
            throw new TimeoutException($"The {toolkit.Name} drag out did not end within 15 seconds. Stages: {Stages()}");
        }
        string[]? uris = null;
        if (!escape)
        {
            // A drag that ended without a drop delivers no URIs; say so at once instead of waiting for them.
            if (effect == DesktopDragEffects.None)
                throw new InvalidOperationException($"The {toolkit.Name} drag out onto the drop target ended with None. Stages: {Stages()}");
            try
            {
                uris = await s_dropped.Task.WaitScaledAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"The {toolkit.Name} drag out ended with {effect}, but its drop target received no URIs within 10 seconds. Stages: {Stages()}");
            }
            Console.WriteLine($"{toolkit.Name} drag out stages: {Stages()}.");
        }
        else
        {
            await Task.Delay(500);
            if (s_dropped.Task.IsCompleted) uris = await s_dropped.Task;
        }
        // The page's mouse state settles before the next press.
        await Task.Delay(300);
        return (effect, uris);
    }

    // D037.2: a drop into a frame of the presented origin is delivered with the frame's label and effect; a drop into a
    // frame of another origin is not, and navigates nothing.
    private static async Task FrameDropsAsync(DesktopHost host, Pointer pointer, string[] paths, Toolkit toolkit)
    {
        var root = Directory.CreateTempSubdirectory("runic-frame-drop-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), FramePage);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "frame.html"), FrameDocument);
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Directory(root.FullName) });
            await using var window = await surface.OpenWindowAsync(toolkit.PageWindow);
            Console.WriteLine($"{toolkit.Name} frame drops: the frame page opened.");
            await surface.ExecuteJavaScriptAsync("return document.readyState;", TimeSpan.FromSeconds(10));
            await Task.Delay(700);
            var before = await surface.ExecuteJavaScriptAsync("return location.href;", TimeSpan.FromSeconds(10));
            var drops = Channel.CreateUnbounded<DesktopFileDropEventArgs>();
            window.FilesDropped += (_, drop) => drops.Writer.TryWrite(drop);
            var uris = paths.Select(static path => new Uri(path).AbsoluteUri).ToArray();
            nint source = 0;
            await window.InvokeNativeAsync(_ => source = toolkit.CreateDragSource(uris));
            try
            {
                await Task.Delay(700);
                Console.WriteLine($"{toolkit.Name} frame drops: dragging into the same-origin frame.");
                await pointer.DragAsync(toolkit.SourcePoint, toolkit.SameFrame);
                var drop = await drops.Reader.ReadAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(15));
                if (drop.Target != "frame-folder" || drop.Effect != DesktopDragEffects.Move || !drop.Paths.SequenceEqual(paths))
                    throw new InvalidOperationException(
                        $"The {toolkit.Name} same-origin frame's drop reported target '{drop.Target}', {drop.Effect} and [{string.Join(", ", drop.Paths)}].");
                Console.WriteLine($"{toolkit.Name} frame drops: the same-origin frame delivered target '{drop.Target}' with {drop.Effect}.");
                await Task.Delay(500);
                await pointer.DragAsync(toolkit.SourcePoint, toolkit.OtherFrame);
                await Task.Delay(2000);
                if (drops.Reader.TryRead(out var foreign))
                    throw new InvalidOperationException($"A {toolkit.Name} drop into a frame of another origin was delivered with target '{foreign.Target}'.");
                var after = await surface.ExecuteJavaScriptAsync("return location.href;", TimeSpan.FromSeconds(10));
                if (after != before) throw new InvalidOperationException($"The {toolkit.Name} window moved from {before} to {after}.");
                Console.WriteLine($"{toolkit.Name} frame drops passed: the same-origin frame's drop carried its label and Move; the data: frame delivered nothing.");
            }
            finally
            {
                await window.InvokeNativeAsync(_ => toolkit.DestroyWindow(source));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static TaskCompletionSource<string[]> NewDrop() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
