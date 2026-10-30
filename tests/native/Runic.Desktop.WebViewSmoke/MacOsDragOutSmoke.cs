using Runic.Desktop;
using Runic.Platform;

// B11 on the macOS runner, without OS input: the window offers FileDrag, and a drag asked for while no mouse button is
// held returns None at once instead of starting an AppKit drag that follows no pointer. The drag loop itself (Finder,
// Mail) is checked by hand.
internal static class MacOsDragOutSmoke
{
    internal static async Task ExerciseAsync(DesktopWindow window)
    {
        if ((window.Capabilities & DesktopWindowCapabilities.FileDrag) == 0)
            throw new InvalidOperationException("Drag out: the WKWebView window does not offer FileDrag.");
        var effect = await window.StartDragAsync(new DesktopDragData
        {
            Text = "Runic drag out",
            Uris = [new Uri("https://example.com/runic")],
        }).AsTask().WaitScaledAsync(TimeSpan.FromSeconds(10));
        if (effect != DesktopDragEffects.None)
            throw new InvalidOperationException($"Drag out: without a held button the drag returned {effect}, not None.");
        Console.WriteLine("PASS drag out: FileDrag offered; a drag without a held button returns None.");
    }
}
