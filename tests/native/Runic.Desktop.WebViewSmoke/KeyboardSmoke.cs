using System.Globalization;
using Runic.Desktop;

// Checks the browser keyboard behaviour of a Linux embedded window with real input:
// Tab and Shift+Tab wrap inside the page, also across positive tabindex values, an
// open shadow root, a radio group, an inert region and a modal dialog; and Control
// with +, -, 0 or the wheel zooms a scrollable page unless the page cancels the
// event. The process does not synthesize input. It prints "KEYBOARD SEND <n> <keys>"
// and waits for keyboard-driver.sh to press them in its window and answer <n>. The
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
        // view, which the GTK 4 provider keeps out of the focus chain. GTK 3 on
        // Wayland matches key bindings against the keymap it started with, so it
        // misses the Backspace of a driver that uploads its own keymap, as wtype does.
        if (backend == "gtk4" || Environment.GetEnvironmentVariable("GDK_BACKEND") == "x11"
            || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not { Length: > 0 })
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
        Pass("Control with +, =, - and 0 zooms in steps and resets, also in a text field.");

        await surface.ExecuteJavaScriptAsync("window.claimZoom = true; return true;", StepTimeout);
        await ExpectZoomAsync(surface, "Ctrl+plus", 1.0, settle: true);
        var claimed = await surface.ExecuteJavaScriptAsync("return String(window.claimed);", StepTimeout);
        if (claimed != "1")
        {
            throw new InvalidOperationException($"The page did not receive the Control and + it handles ({claimed}).");
        }
        await surface.ExecuteJavaScriptAsync("window.claimZoom = false; return true;", StepTimeout);
        Pass("Control and + that the page handles does not zoom.");

        // The page is three viewports tall, so a wheel that WebKit handled would scroll it.
        await ExpectZoomAsync(surface, "Ctrl+wheel-up", 1.1);
        await ExpectZoomAsync(surface, "Ctrl+wheel-down", 1.0);
        await ExpectZoomAsync(surface, "Ctrl+wheel-down", 0.9);
        await ExpectScrollAsync(surface, 0);
        await ExpectZoomAsync(surface, "Ctrl+0", 1.0);
        Pass("Control and the wheel that the page leaves alone zooms without scrolling.");

        await surface.ExecuteJavaScriptAsync("window.claimWheel = true; return true;", StepTimeout);
        await ExpectZoomAsync(surface, "Ctrl+wheel-down", 1.0, settle: true);
        var claimedWheel = await surface.ExecuteJavaScriptAsync("return String(window.claimedWheel);", StepTimeout);
        await ExpectScrollAsync(surface, 0);
        if (claimedWheel != "1")
        {
            throw new InvalidOperationException($"The page did not receive the Control and wheel it cancels ({claimedWheel}).");
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

        await ExpectWrapAsync(surface, "positive", ["one", "two", "zero-first", "zero-last"],
            "Positive tabindex values come first when Tab wraps.");
        await ExpectWrapAsync(surface, "shadow", ["shadow-first", "slotted", "after-shadow"],
            "Tab wraps into an open shadow root.");
        await ExpectWrapAsync(surface, "radio", ["radio-b", "after-radio"],
            "Tab wraps into a radio group at its checked button.");
        await ExpectWrapAsync(surface, "inert", ["live-first", "live-last"],
            "Tab wraps past an inert region.");

        await UseAsync(surface, "dialog");
        await surface.ExecuteJavaScriptAsync("document.getElementById('modal').showModal(); return true;", StepTimeout);
        await WaitAsync(surface, state => state.Active == "dialog-first", "focus on the modal dialog", settle: false);
        await ExpectFocusAsync(surface, "Tab", "dialog-last");
        await ExpectFocusAsync(surface, "Tab", "dialog-first");
        // WebKit itself focuses the dialog element before its first control on the way back.
        await SendAsync("Shift+Tab");
        await WaitAsync(surface, state => state.Active is "modal" or "dialog-last", "focus on the dialog or its last control after Shift+Tab", settle: false);
        if ((await ReadAsync(surface)).Active == "modal")
        {
            await ExpectFocusAsync(surface, "Shift+Tab", "dialog-last");
        }
        await surface.ExecuteJavaScriptAsync("document.getElementById('modal').close(); return true;", StepTimeout);
        Pass("Tab wraps inside a modal dialog.");

        // Known limit: WebKit hands focus to the window from the frame's document,
        // which the keyboard script, in the top frame only, does not see. The next Tab
        // starts over at the first control.
        await UseAsync(surface, "frame");
        await ExpectFocusAsync(surface, "Tab", "before-frame");
        await ExpectFocusAsync(surface, "Tab", "inner/in-frame");
        await SendAsync("Tab");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var limit = await ReadAsync(surface);
        if (limit.Active == "before-frame")
        {
            Console.WriteLine("KEYBOARD NOTE Tab after a control in a frame now wraps; update the documented limit.");
        }
        await ExpectFocusAsync(surface, "Tab", "before-frame");
        Pass($"Known limit: Tab after the last control inside a frame stops once ({limit}), then starts over.");

        // Without a control, the document keeps focus.
        await UseAsync(surface, "empty");
        await SendAsync("Tab");
        await WaitAsync(surface, state => state.Active == "none" && state.Detail.Contains("hasFocus=true", StringComparison.Ordinal),
            "a focused document without controls after Tab", settle: true);
        await UseAsync(surface, "basic");
        await ExpectFocusAsync(surface, "Tab", "first");
        Pass("A page without controls keeps focus after Tab.");

        Console.WriteLine("KEYBOARD DONE");
        await window.CloseAsync();
    }

    // Tab walks the section from an unfocused page, wraps to its first control and
    // back with Shift+Tab.
    private static async Task ExpectWrapAsync(DesktopSurface surface, string section, string[] order, string message)
    {
        await UseAsync(surface, section);
        foreach (var expected in order)
        {
            await ExpectFocusAsync(surface, "Tab", expected);
        }
        await ExpectFocusAsync(surface, "Tab", order[0]);
        await ExpectFocusAsync(surface, "Shift+Tab", order[^1]);
        Pass(message);
    }

    private static async Task UseAsync(DesktopSurface surface, string section)
    {
        await surface.ExecuteJavaScriptAsync($"use('{section}'); return true;", StepTimeout);
        await WaitAsync(surface, state => state.Active == "none", $"no focus in section {section}", settle: false);
    }

    private static async Task ExpectFocusAsync(DesktopSurface surface, string keys, string expected, bool settle = false)
    {
        await SendAsync(keys);
        await WaitAsync(surface, state => state.Active == expected, $"focus on '{expected}' after {keys}", settle);
    }

    private static async Task ExpectScrollAsync(DesktopSurface surface, int expected)
    {
        var scroll = await surface.ExecuteJavaScriptAsync("return String(Math.round(scrollY));", StepTimeout);
        if (scroll != expected.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException($"Expected the page at scroll position {expected}; it is at {scroll}.");
        }
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
        var focusLog = await surface.ExecuteJavaScriptAsync("return window.focusLog.join(',');", StepTimeout);
        throw new TimeoutException($"Expected {description}; the page reports {state}; focus events: {focusLog}.");
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
    // by a window manager, so the viewport width is not a stable measure. Each
    // section is one Tab scenario; use() shows one and clears focus.
    private const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic keyboard smoke</title>
        <style>body{font:20px sans-serif;margin:2rem}button,input{font:inherit;margin:.5rem;padding:.5rem 1rem}
        :focus-visible{outline:4px solid #c2185b;outline-offset:3px}</style></head>
        <body><main><h1>Keyboard</h1><p id="state" aria-live="polite"></p>
        <section id="basic"><p>Tab wraps; Control and + zooms.</p>
          <button id="first">First</button><button id="middle">Middle</button><input id="last" aria-label="Last" autocomplete="off"></section>
        <section id="positive" hidden><button id="zero-first">Zero first</button><button id="two" tabindex="2">Two</button>
          <button id="one" tabindex="1">One</button><button id="zero-last">Zero last</button></section>
        <section id="shadow" hidden><div id="host"><button id="slotted">Slotted</button></div><button id="after-shadow">After shadow</button></section>
        <section id="radio" hidden><fieldset><legend>Choice</legend><input type="radio" name="choice" id="radio-a" aria-label="A">
          <input type="radio" name="choice" id="radio-b" aria-label="B" checked><input type="radio" name="choice" id="radio-c" aria-label="C"></fieldset>
          <button id="after-radio">After radio</button></section>
        <section id="inert" hidden><div inert><button id="inert-button">Inert</button></div><button id="live-first">Live first</button><button id="live-last">Live last</button></section>
        <section id="dialog" hidden><button id="behind-dialog">Behind dialog</button>
          <dialog id="modal"><button id="dialog-first">Dialog first</button><button id="dialog-last">Dialog last</button></dialog></section>
        <section id="frame" hidden><button id="before-frame">Before frame</button>
          <iframe id="inner" title="Inner" srcdoc="<button id=in-frame>In frame</button>"></iframe></section>
        <section id="empty" hidden><p>No controls.</p></section>
        <div style="height:300vh" aria-hidden="true"></div></main>
        <script>
        const baseRatio = devicePixelRatio;
        document.getElementById("host").attachShadow({ mode: "open" }).innerHTML = '<button id="shadow-first">Shadow first</button><slot></slot>';
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
        window.use = name => {
          for (const section of document.querySelectorAll("section")) section.hidden = section.id !== name;
          document.activeElement?.blur();
          scrollTo(0, 0);
        };
        function active() {
          let element = document.activeElement;
          if (!element || element === document.body) return "none";
          if (element.tagName === "IFRAME") return element.id + "/" + (element.contentDocument?.activeElement?.id || "none");
          while (element.shadowRoot?.activeElement) element = element.shadowRoot.activeElement;
          return element.id;
        }
        window.keyboardState = () => {
          const zoom = devicePixelRatio / baseRatio;
          document.getElementById("state").textContent = "Zoom " + Math.round(zoom * 100) + " %";
          return active() + "|" + zoom.toFixed(4) + "|dpr=" + devicePixelRatio + " hasFocus=" + document.hasFocus() + " scrollY=" + Math.round(scrollY) + " keys=" + keyLog.slice(-6).join(",");
        };
        </script></body></html>
        """;
}
