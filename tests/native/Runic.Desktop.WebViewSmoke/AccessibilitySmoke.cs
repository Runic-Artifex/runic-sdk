using System.Diagnostics;
using Runic.Desktop;

// --accessibility: the page a screen reader meets, read and changed through the platform's accessibility API by a
// separate process, tests/native/Runic.Desktop.AccessibilityProbe, as a screen reader reads it. The fixture opens the
// window, starts the probe with its process ID and does what the page asks of the host: AnnounceAsync. It passes when
// the probe passes and has pressed Finish. The GTK 4 smoke links this file for GTK 3 and GTK 4, and the WPF Windows
// tests for a RunicWebView, whose WPF window announces through WpfPlatformServices (announce).
internal static class AccessibilitySmoke
{
    // The page's controls; the probe looks for them by these names (AccessibilityProbe/Fixture.cs).
    internal const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic accessibility fixture</title>
        <style>body { font: 16px system-ui; } [role=option][aria-selected=true] { font-weight: bold; }</style></head>
        <body><main><h1>Accessibility fixture</h1>
        <p><label for="volume">Volume</label> <input id="volume" type="range" min="0" max="10" step="1" value="3"></p>
        <p><label for="copies">Copies</label> <input id="copies" type="number" min="1" max="9" value="2"></p>
        <p><span id="keep" role="spinbutton" tabindex="0" aria-label="Restore points to keep"
              aria-valuenow="5" aria-valuemin="1" aria-valuemax="50" aria-valuetext="5 restore points">5</span>
           <button id="more" type="button">Keep more</button></p>
        <p><label for="fruit">Fruit</label>
           <select id="fruit" size="3"><option>Apple</option><option selected>Pear</option><option>Plum</option></select></p>
        <ul id="colour" role="listbox" aria-label="Colour" tabindex="0" aria-activedescendant="red">
          <li id="red" role="option" aria-selected="true">Red</li>
          <li id="green" role="option" aria-selected="false">Green</li>
          <li id="blue" role="option" aria-selected="false">Blue</li></ul>
        <button id="next-colour" type="button">Next colour</button>
        <p><output id="status" aria-live="polite">Nothing saved yet</output></p>
        <button id="save" type="button">Save</button>
        <button id="announce" type="button">Announce</button>
        <button id="alert" type="button">Alert</button>
        <button id="finish" type="button">Finish</button></main>
        <script>
        const keep = document.getElementById('keep');
        function setKeep(value) {
          value = Math.min(50, Math.max(1, value));
          keep.setAttribute('aria-valuenow', String(value));
          keep.setAttribute('aria-valuetext', value + ' restore points');
          keep.textContent = String(value);
        }
        keep.addEventListener('keydown', event => {
          const now = Number(keep.getAttribute('aria-valuenow'));
          const step = { ArrowUp: 1, ArrowDown: -1, PageUp: 10, PageDown: -10 }[event.key];
          if (step) setKeep(now + step);
          else if (event.key === 'Home') setKeep(1);
          else if (event.key === 'End') setKeep(50);
          else return;
          event.preventDefault();
        });
        document.getElementById('more').addEventListener('click', () => setKeep(Number(keep.getAttribute('aria-valuenow')) + 1));
        const colours = [...document.querySelectorAll('#colour [role=option]')];
        function selectColour(option) {
          for (const other of colours) other.setAttribute('aria-selected', String(other === option));
          document.getElementById('colour').setAttribute('aria-activedescendant', option.id);
        }
        for (const option of colours) option.addEventListener('click', () => selectColour(option));
        document.getElementById('next-colour').addEventListener('click', () => {
          const current = colours.findIndex(option => option.getAttribute('aria-selected') === 'true');
          selectColour(colours[(current + 1) % colours.length]);
        });
        let saved = 0;
        document.getElementById('save').addEventListener('click', () => {
          document.getElementById('status').textContent = 'Saved version ' + (++saved);
        });
        document.getElementById('announce').addEventListener('click', () => { window.__runicRequest = 'announce'; });
        document.getElementById('alert').addEventListener('click', () => { window.__runicRequest = 'alert'; });
        document.getElementById('finish').addEventListener('click', () => { window.__runicRequest = 'finish'; });
        </script></body></html>
        """;

    // --region: a host page whose region shows a guest's page in a cross-site frame, as region mode does (#866); the probe
    // (--region) finds and presses the guest's button inside the region through the accessibility API, then the host's
    // Finish. The guest's page tells the host page of the press.
    internal const string RegionPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic accessibility fixture</title>
        <style>body { font: 16px system-ui; } iframe { width: 100%; height: 260px; border: 1px solid #888; }</style></head>
        <body><main><h1>Region fixture</h1>
        <iframe id="region" sandbox="allow-scripts allow-same-origin allow-forms" title="Region guest"></iframe>
        <button id="finish" type="button">Finish</button></main>
        <script>
        window.__regionPressed = false;
        const frame = document.getElementById('region');
        addEventListener('message', event => { if (event.source === frame.contentWindow && event.data === 'pressed') window.__regionPressed = true; });
        document.getElementById('finish').addEventListener('click', () => { window.__runicRequest = 'finish'; });
        </script></body></html>
        """;

    internal const string RegionGuestPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Region guest</title></head>
        <body><main><h1>System check</h1><button id="run" type="button">Run system check</button></main>
        <script>document.getElementById('run').addEventListener('click', () => parent.postMessage('pressed', '*'));</script></body></html>
        """;

    // --side-panel (sdk#864 P6): a page with a side panel open on its right edge (DesktopWindow.OpenSidePanelAsync). The
    // panel's page (#side-panel) is a named complementary landmark with one button; the page's Dock panel left asks the
    // fixture to move the panel. Where the host refuses the panel (Unsupported), the fixture adds the page's "Side panel
    // unsupported" button instead, which tells the probe (AccessibilityProbe/SidePanelScenario.cs).
    internal const string SidePanelPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic accessibility fixture</title><style>body { font: 16px system-ui; }</style>
        <script>
        addEventListener('DOMContentLoaded', () => {
          if (location.hash !== '#side-panel') return;
          document.title = 'Side panel';
          document.body.innerHTML = '<aside aria-label="Debug tools"><button id="panel" type="button">Panel action</button></aside>';
        });
        </script></head>
        <body><main><h1>Side panel fixture</h1>
        <button id="page" type="button">Page action</button>
        <button id="dock" type="button">Dock panel left</button>
        <button id="finish" type="button">Finish</button></main>
        <script>
        document.getElementById('dock').addEventListener('click', () => { window.__runicRequest = 'dock-left'; });
        document.getElementById('finish').addEventListener('click', () => { window.__runicRequest = 'finish'; });
        </script></body></html>
        """;

    internal const string PoliteAnnouncement = "Runic polite announcement";
    internal const string AssertiveAnnouncement = "Runic assertive announcement";

    // probe: the probe's command and its own arguments, everything after --probe, e.g.
    // "--probe dotnet path/Runic.Desktop.AccessibilityProbe.dll --receipt receipt.json"; the fixture adds --pid and
    // --host: gtk3, gtk4, webview2, wkwebview or wpf, which selects the probe's expectations.
    internal static IReadOnlyList<string> ProbeCommand(string[] args)
    {
        var index = Array.IndexOf(args, "--probe");
        return index >= 0 && index + 1 < args.Length
            ? args[(index + 1)..]
            : throw new ArgumentException("--accessibility needs --probe <command> [arguments].");
    }

    // announce: how the host announces, when not through the window's DesktopWindow.AnnounceAsync.
    internal static async Task<int> RunAsync(DesktopHost host, string hostName, IReadOnlyList<string> probe,
        Func<string, DesktopAnnouncementPriority, CancellationToken, Task>? announce = null, bool region = false, bool sidePanel = false)
    {
        if (region) probe = [.. probe, "--region"];
        if (sidePanel) probe = [.. probe, "--side-panel"];
        // The probe starts first and registers for events, as a screen reader runs before the application: WebKitGTK's
        // web process reads the registered events when it starts.
        var start = new ProcessStartInfo(probe[0]) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in probe.Skip(1)) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(hostName);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The probe did not start.");
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                Console.WriteLine(line);
                if (line == "PROBE LISTENING") listening.TrySetResult();
            }
            listening.TrySetException(new InvalidOperationException("The probe exited before it listened for events."));
        });
        await listening.Task.WaitScaledAsync(TimeSpan.FromSeconds(60));

        await using var guest = region ? await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(RegionGuestPage) }) : null;
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(region ? RegionPage : sidePanel ? SidePanelPage : Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 720,
            Height = 640,
        });
        await window.FocusAsync();
        if (announce is null && !window.Capabilities.HasFlag(DesktopWindowCapabilities.Announcements))
            throw new InvalidOperationException("The embedded window does not report announcements.");
        announce ??= (message, priority, cancellationToken) => window.AnnounceAsync(message, priority, cancellationToken).AsTask();
        if (await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(20)) != "Runic accessibility fixture")
            throw new InvalidOperationException("The fixture page did not load.");
        if (guest is not null)
        {
            // The guest's frame launch address on the other loopback family, as region mode's guest answers region-attach.
            var launch = await guest.BeginFrameLaunchAsync(Runic.Desktop.Internal.RegionFrames.Origin(surface.Url), allowSameSite: true,
                measuresHeight: false, CancellationToken.None);
            await surface.ExecuteJavaScriptAsync(
                $"document.getElementById('region').src = {System.Text.Json.Nodes.JsonValue.Create(launch.Url.AbsoluteUri).ToJsonString()}; return '';");
            Console.WriteLine($"ACCESSIBILITY region frame from {Runic.Desktop.Internal.RegionFrames.Origin(launch.Url)}{(launch.SameSite ? " (same-site)" : "")}.");
        }
        var panelOpened = false;
        if (sidePanel)
        {
#pragma warning disable RUNICDESK104 // The side panel is experimental.
            if (window.CheckSidePanel(DesktopWindowEdge.Right, 260) == DesktopSidePanelRefusal.Unsupported)
            {
                await surface.ExecuteJavaScriptAsync("""
                    const unsupported = document.createElement('button');
                    unsupported.type = 'button';
                    unsupported.textContent = 'Side panel unsupported';
                    unsupported.addEventListener('click', () => { window.__runicRequest = 'finish'; });
                    document.querySelector('main').append(unsupported);
                    return '';
                    """);
                Console.WriteLine($"ACCESSIBILITY side panel: {hostName} refuses it with Unsupported.");
            }
            else
            {
                var opened = await window.OpenSidePanelAsync(new DesktopSidePanelOptions { Edge = DesktopWindowEdge.Right, Width = 260, Path = "#side-panel" });
                if (opened is not DesktopSidePanelResult.Opened)
                    throw new InvalidOperationException($"The side panel fixture's panel did not open: {opened}.");
                panelOpened = true;
                Console.WriteLine("ACCESSIBILITY side panel open on the right edge.");
            }
#pragma warning restore RUNICDESK104
        }
        Console.WriteLine($"ACCESSIBILITY READY host={hostName}.");
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromMinutes(5));
        var exited = process.WaitForExitAsync(deadline.Token);
        var finished = false;
        while (!exited.IsCompleted)
        {
            var request = await surface.ExecuteJavaScriptAsync(
                "const request = window.__runicRequest || ''; window.__runicRequest = ''; return request;",
                TimeSpan.FromSeconds(20), cancellationToken: deadline.Token);
            if (request == "announce")
            {
                await announce(PoliteAnnouncement, DesktopAnnouncementPriority.Polite, deadline.Token);
                Console.WriteLine("ACCESSIBILITY announced the polite message.");
            }
            else if (request == "alert")
            {
                await announce(AssertiveAnnouncement, DesktopAnnouncementPriority.Assertive, deadline.Token);
                Console.WriteLine("ACCESSIBILITY announced the assertive message.");
            }
            else if (request == "dock-left")
            {
#pragma warning disable RUNICDESK104 // The side panel is experimental.
                var refusal = await window.SidePanel!.MoveToEdgeAsync(DesktopWindowEdge.Left, deadline.Token);
#pragma warning restore RUNICDESK104
                if (refusal is not null) throw new InvalidOperationException($"The side panel fixture's panel did not move to the left edge: {refusal}.");
                Console.WriteLine("ACCESSIBILITY side panel moved to the left edge.");
            }
            else if (request == "finish")
            {
                finished = true;
            }
            await Task.WhenAny(exited, Task.Delay(50, deadline.Token));
        }
        await exited;
        await output;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The accessibility probe failed with exit code {process.ExitCode}.");
        // The probe pressed Finish through the accessibility API before it exited.
        if (!finished && await surface.ExecuteJavaScriptAsync("return window.__runicRequest || '';") != "finish")
            throw new InvalidOperationException("The probe exited without pressing Finish through the accessibility API.");
        if (region && await surface.ExecuteJavaScriptAsync("return String(window.__regionPressed);") != "true")
            throw new InvalidOperationException("The probe's press of the guest's button in the region did not reach the guest's page.");
        if (sidePanel)
        {
            Console.WriteLine(panelOpened
                ? $"PASS {hostName} accessibility side panel: the probe read the panel beside the page through the platform accessibility API."
                : $"NOT VERIFIED {hostName} accessibility side panel: the host refuses the panel with Unsupported.");
            return 0;
        }
        if (region)
        {
            Console.WriteLine($"PASS {hostName} accessibility region: the probe found and pressed the guest's button inside the region through the platform accessibility API.");
            return 0;
        }
        Console.WriteLine($"PASS {hostName} accessibility fixture: the probe read and changed the page through the platform accessibility API.");
        return 0;
    }
}
