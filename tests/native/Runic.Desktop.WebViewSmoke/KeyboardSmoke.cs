using System.Globalization;
using Runic.Desktop;

// Checks the browser keyboard behaviour of a Linux embedded window with real input:
// Tab and Shift+Tab wrap inside the page, and Control with +, -, 0 or the wheel zooms
// a scrollable page unless the page cancels the event. The process does not
// synthesize input. It prints "KEYBOARD SEND <n> <keys>" and waits for a driver, such
// as keyboard-wayland.sh, to press them in the focused window and answer <n>. The
// GTK 4 smoke links this file and runs the same checks on its provider.
internal static class KeyboardSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(DesktopHostOptions hostOptions, string backend)
    {
        await using var host = await DesktopHost.StartAsync(hostOptions);
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 640,
            Height = 480,
        });
        await window.FocusAsync();
        var initial = await ReadAsync(surface);
        Console.WriteLine($"KEYBOARD READY backend={backend} {initial}");

        await ExpectFocusAsync(surface, "Tab", "first");
        await ExpectFocusAsync(surface, "Tab", "middle");
        await ExpectFocusAsync(surface, "Tab", "last");
        await Shot("tab-last");
        await surface.ExecuteJavaScriptAsync("window.focusLog = []; return true;", StepTimeout);
        await ExpectFocusAsync(surface, "Tab", "first");
        var forward = await surface.ExecuteJavaScriptAsync("return window.focusLog.join(',');", StepTimeout);
        Pass($"Tab after the last control focuses the first one (focus events: {forward}).");
        await Shot("tab-wrapped");
        await surface.ExecuteJavaScriptAsync("window.focusLog = []; return true;", StepTimeout);
        await ExpectFocusAsync(surface, "Shift+Tab", "last");
        var backward = await surface.ExecuteJavaScriptAsync("return window.focusLog.join(',');", StepTimeout);
        Pass($"Shift+Tab before the first control focuses the last one (focus events: {backward}).");

        // A page that cancels Tab keeps focus where it put it.
        await surface.ExecuteJavaScriptAsync("window.trapTab = true; return true;", StepTimeout);
        await ExpectFocusAsync(surface, "Tab", "last", settle: true);
        await surface.ExecuteJavaScriptAsync("window.trapTab = false; return true;", StepTimeout);
        Pass("A Tab the page cancels does not wrap.");

        await SendAsync("type:ab");
        var typed = "ab";
        // WebKit translates editing keys such as Backspace through a hidden GTK text
        // view, which the GTK 4 provider keeps out of the focus chain. GTK 3 matches
        // key bindings against the keymap it started with, so it misses the Backspace
        // of a driver that uploads its own keymap, such as wtype.
        if (backend == "gtk4")
        {
            await SendAsync("BackSpace");
            typed = "a";
        }
        await ExpectValueAsync(surface, typed);
        Pass($"Typing edits the focused field ({typed}).");

        await ExpectZoomAsync(surface, "Ctrl+plus", 1.1);
        await ExpectZoomAsync(surface, "Ctrl+equal", 1.2);
        await Shot("zoom-120");
        await ExpectZoomAsync(surface, "Ctrl+minus", 1.1);
        await ExpectZoomAsync(surface, "Ctrl+0", 1.0);
        await ExpectZoomAsync(surface, "Ctrl+minus", 0.9);
        await ExpectZoomAsync(surface, "Ctrl+0", 1.0);
        await ExpectValueAsync(surface, typed);
        Pass("Control with +, =, - and 0 zooms in steps and resets.");

        await surface.ExecuteJavaScriptAsync("window.claimZoom = true; return true;", StepTimeout);
        await ExpectZoomAsync(surface, "Ctrl+plus", 1.0, settle: true);
        var claimed = await surface.ExecuteJavaScriptAsync("return String(window.claimed);", StepTimeout);
        if (claimed != "1")
        {
            throw new InvalidOperationException($"The page did not receive the Control and + it handles ({claimed}).");
        }
        await surface.ExecuteJavaScriptAsync("window.claimZoom = false; return true;", StepTimeout);
        Pass("Control and + that the page handles does not zoom.");

        await ExpectZoomAsync(surface, "Ctrl+wheel-up", 1.1);
        await ExpectZoomAsync(surface, "Ctrl+wheel-down", 1.0);
        Pass("Control and the wheel zooms.");

        await surface.ExecuteJavaScriptAsync("window.claimWheel = true; return true;", StepTimeout);
        await ExpectZoomAsync(surface, "Ctrl+wheel-down", 1.0, settle: true);
        var claimedWheel = await surface.ExecuteJavaScriptAsync("return String(window.claimedWheel) + ' ' + scrollY;", StepTimeout);
        if (claimedWheel != "1 0")
        {
            throw new InvalidOperationException($"The page did not cancel the Control and wheel it handles (events, scroll: {claimedWheel}).");
        }
        await surface.ExecuteJavaScriptAsync("window.claimWheel = false; return true;", StepTimeout);
        Pass("Control and the wheel that the page cancels neither zooms nor scrolls.");

        for (var step = 0; step < 16; step++)
        {
            await SendAsync("Ctrl+plus");
        }
        await ExpectZoomAsync(surface, "Ctrl+plus", 3.0);
        Pass("Zoom stops at 300 %.");
        await Shot("zoom-300");
        await ExpectZoomAsync(surface, "Ctrl+0", 1.0);
        Console.WriteLine("KEYBOARD DONE");
        await window.CloseAsync();
    }

    private static async Task ExpectFocusAsync(DesktopSurface surface, string keys, string expected, bool settle = false)
    {
        await SendAsync(keys);
        await WaitAsync(surface, state => state.Active == expected, $"focus on '{expected}' after {keys}", settle);
    }

    private static async Task ExpectValueAsync(DesktopSurface surface, string expected)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        string value;
        do
        {
            value = await surface.ExecuteJavaScriptAsync("return document.getElementById('last').value;", StepTimeout);
            if (value == expected)
            {
                return;
            }
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Expected the field to contain '{expected}'; it contains '{value}'; the page reports {await ReadAsync(surface)}.");
    }

    private static async Task ExpectZoomAsync(DesktopSurface surface, string keys, double expected, bool settle = false)
    {
        await SendAsync(keys);
        await WaitAsync(surface, state => Math.Abs(state.Zoom - expected) < 0.02, $"zoom {expected:0.##} after {keys}", settle);
    }

    private static async Task WaitAsync(DesktopSurface surface, Func<PageState, bool> check, string description, bool settle)
    {
        if (settle)
        {
            // A negative check: give a wrong result time to appear.
            await Task.Delay(TimeSpan.FromSeconds(1.5));
        }
        var deadline = DateTime.UtcNow + StepTimeout;
        PageState state;
        do
        {
            state = await ReadAsync(surface);
            if (check(state))
            {
                Console.WriteLine($"KEYBOARD OK {description}: {state}");
                return;
            }
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Expected {description}; the page reports {state} focus events: {await surface.ExecuteJavaScriptAsync("return window.focusLog.join(',');", StepTimeout)}.");
    }

    private static async Task SendAsync(string keys)
    {
        var request = Interlocked.Increment(ref _requests);
        Console.WriteLine($"KEYBOARD SEND {request} {keys}");
        await WaitForDriverAsync(request);
    }

    private static async Task Shot(string name)
    {
        var request = Interlocked.Increment(ref _requests);
        Console.WriteLine($"KEYBOARD SHOT {request} {name}");
        await WaitForDriverAsync(request);
    }

    private static int _requests;

    // The driver acknowledges each request by its number on standard input.
    private static async Task WaitForDriverAsync(int request)
    {
        using var timeout = new CancellationTokenSource(StepTimeout);
        while (await Console.In.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Trim() == request.ToString(CultureInfo.InvariantCulture))
            {
                return;
            }
        }
        throw new InvalidOperationException("The keyboard driver closed its input.");
    }

    private static void Pass(string message) => Console.WriteLine("PASS " + message);

    private static async Task<PageState> ReadAsync(DesktopSurface surface)
    {
        var value = await surface.ExecuteJavaScriptAsync("return window.keyboardState();", StepTimeout);
        var parts = value.Split('|');
        return new PageState(parts[0], double.Parse(parts[1], CultureInfo.InvariantCulture), parts[2]);
    }

    private readonly record struct PageState(string Active, double Zoom, string Detail)
    {
        public override string ToString() => $"active={Active} zoom={Zoom:0.###} {Detail}";
    }

    // WebKit includes the page zoom in devicePixelRatio. The window can be resized
    // by a tiling compositor, so the viewport width is not a stable measure.
    private const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic keyboard smoke</title>
        <style>body{font:20px sans-serif;margin:2rem}button{font:inherit;margin:.5rem;padding:.5rem 1rem}
        :focus-visible{outline:4px solid #c2185b;outline-offset:3px}</style></head>
        <body><main><h1>Keyboard</h1><p>Tab wraps; Control and + zooms.</p>
        <button id="first">First</button><button id="middle">Middle</button><input id="last" aria-label="Last" autocomplete="off">
        <p id="state" aria-live="polite"></p><div style="height:300vh" aria-hidden="true"></div></main>
        <script>
        const baseRatio = devicePixelRatio;
        window.focusLog = [];
        window.claimed = 0;
        window.claimedWheel = 0;
        addEventListener("focusin", event => focusLog.push(event.target.id || event.target.tagName));
        addEventListener("blur", event => { if (event.target === window) focusLog.push("window-blur"); });
        addEventListener("focus", event => { if (event.target === window) focusLog.push("window-focus"); });
        window.keyLog = [];
        addEventListener("keydown", event => { if (!event.repeat) keyLog.push("down:" + (event.shiftKey ? "Shift+" : "") + event.key); }, true);
        addEventListener("keyup", event => keyLog.push("up:" + event.key), true);
        addEventListener("wheel", event => keyLog.push("wheel:" + Math.round(event.deltaY) + "/" + event.deltaMode + (event.ctrlKey ? "+ctrl" : "")), { capture: true, passive: true });
        document.addEventListener("keydown", event => {
          if (window.trapTab && event.key === "Tab") event.preventDefault();
          if (window.claimZoom && event.ctrlKey && (event.key === "+" || event.key === "=")) { event.preventDefault(); window.claimed++; }
        });
        document.addEventListener("wheel", event => {
          if (window.claimWheel && event.ctrlKey) { event.preventDefault(); window.claimedWheel++; }
        }, { passive: false });
        window.keyboardState = () => {
          const zoom = devicePixelRatio / baseRatio;
          const active = document.activeElement && document.activeElement !== document.body ? document.activeElement.id : "none";
          document.getElementById("state").textContent = "Zoom " + Math.round(zoom * 100) + " %";
          return active + "|" + zoom.toFixed(4) + "|dpr=" + devicePixelRatio + " width=" + document.documentElement.clientWidth + " hasFocus=" + document.hasFocus() + " scrollY=" + scrollY + " keys=" + keyLog.slice(-6).join(",");
        };
        </script></body></html>
        """;
}
