using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;

#pragma warning disable RUNICDESK103 // Hosted regions and their appearance override are experimental.

// --region-appearance (region-appearance/1, #917 H4a): this executable hosts itself as a guest whose Window shows in a
// region of the host's page with an appearance override (RunicHostedRegionOptions.Appearance: more contrast and the
// colour scheme other than the host page's own, dark on a light desktop), in the platform's web view (WebView2,
// WKWebView, WebKitGTK 4.1) or with gtk4 in WebKitGTK 6.
// - Asserted: the guest page's first appearance push carries the override, before the page's first paint marker, and
//   a RunicHostedRegion.SetAppearanceAsync change reaches the page and the host page's frame.
// - Recorded, not asserted (ENGINE-RECORD lines): whether the frame's prefers-color-scheme follows the CSS color-scheme
//   of the frame element, for color-scheme: dark and only dark (light and only light on a dark desktop), as the
//   content sets it and after a change. Until every engine is recorded the frame's media query is best effort
//   (D066.10, docs/hosted-regions.md).
// Both pages speak the Views protocol directly through the Desktop transport (__runicDesktopViews), so the smoke needs
// no frontend build: the host page plays <runic-hosted-region> (hosted-regions/1; it sets the frame's color-scheme
// from colorScheme as the element does) and the guest page reads the appearance push (appearance/1).
internal static class RegionAppearanceSmoke
{
    internal const string Argument = "--region-appearance";
    internal const string GuestBehaviour = "region-appearance";
    private const string Region = "probe";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    // How long the record waits for the frame's media query to settle after a change of the frame's color-scheme.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);


    // The host. Must run before the first await: on macOS the AppKit loop owns the main thread.
    internal static int Run(DesktopHostOptions options, string toolkit)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new ConsoleLogs());
        services.AddRunicHostedApplications();
        AddWindow(services);
        using var provider = services.BuildServiceProvider();
        return RunicDesktopHost.Run(provider, options,
            new RunicDesktopHostOptions { SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html(HostPage) } },
            host => RunHostAsync(host, toolkit));
    }

    private static async Task<int> RunHostAsync(RunicDesktopHost host, string toolkit)
    {
        await using var hostWindow = await host.OpenWindowAsync<HostedSmokeWindow>(new RunicWindowOptions { Width = 640, Height = 480 });
        var surface = RunicDesktopHost.GetSurface(hostWindow) ?? throw new InvalidOperationException("The host's Window has no surface.");
        await WaitForAsync(surface, "window.__declared === true", "The host page did not declare its region.");
        // The override's scheme is the other one than the host page's own (the desktop's, light on the CI runners), so
        // that the frame's media query shows whether it follows the frame's color-scheme or the host page's.
        var hostDark = bool.Parse(await surface.ExecuteJavaScriptAsync("return String(matchMedia('(prefers-color-scheme: dark)').matches);"));
        var (target, other) = hostDark ? ("light", "dark") : ("dark", "light");
        var initial = Appearance(target);
        var changed = Appearance(other);
        Console.WriteLine($"ENGINE-RECORD {toolkit} host-page prefers-dark={Lower(hostDark)}");

        var opened = new TaskCompletionSource<RunicHostedWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.HostedApplications.WindowOpened += (_, e) => opened.TrySetResult(e.Window);
        var policy = new Policy(initial);
        await using var application = await host.HostedApplications.StartAsync(Guest(), new RunicHostedApplicationOptions { Policy = policy });
        var window = await opened.Task.WaitAsync(TestTimeouts.Scale(Wait));
        Check(policy.Request is { CanEmbed: true, CanOverrideAppearance: true }, "The guest's window can take no region or no appearance override.");
        var region = window.Region ?? throw new InvalidOperationException("The guest's window shows in no region.");
        Check(region.CanSetAppearance && region.Appearance == initial, $"The region's appearance is {region.Appearance}, not {initial}.");

        // Asserted: the first appearance push, before the first paint marker.
        var content = await HostLogAsync(surface, "hosted-region-content");
        Check(content["colorScheme"]?.GetValue<string>() == target, $"The host page's content push was {content.ToJsonString()}.");
        var first = await GuestMessageAsync(surface, "appearance");
        var painted = await GuestMessageAsync(surface, "painted");
        Check(first["first"]!.GetValue<bool>() && first["beforePaint"]!.GetValue<bool>() && painted["appearanceBefore"]!.GetValue<bool>(),
            $"The guest page's first appearance push did not arrive before its first paint marker: {first.ToJsonString()}, {painted.ToJsonString()}.");
        CheckAppearance(first, target, "more");
        Console.WriteLine($"PASS region appearance {toolkit}: the guest page's first appearance push, before its first paint marker, " +
            $"carries the override (host {Show(first["host"])}, combined {Show(first["combined"])}).");

        // Recorded: the frame's media query under the color-scheme the content set.
        var records = new List<(string Scheme, string When, bool Expected, bool Seen)>
        {
            await RecordAsync(surface, toolkit, target, "load", hostDark),
        };

        // Asserted: a change through SetAppearanceAsync reaches the guest's page and the host page's frame.
        Check(await region.SetAppearanceAsync(changed), "SetAppearanceAsync did not apply the change.");
        var update = await HostLogAsync(surface, "hosted-region-update");
        Check(update["colorScheme"]?.GetValue<string>() == other, $"The host page's update was {update.ToJsonString()}.");
        var pushed = await GuestMessageAsync(surface, "appearance");
        CheckAppearance(pushed, other, "more");
        Console.WriteLine($"PASS region appearance {toolkit}: SetAppearanceAsync changed the guest page's appearance (host {Show(pushed["host"])}, " +
            $"combined {Show(pushed["combined"])}) and the frame's color-scheme to {other}.");
        records.Add(await RecordAsync(surface, toolkit, other, "change", hostDark));

        // Recorded: "only <scheme>", then the plain keyword as a change after the load, as hosted-region-update sets it.
        records.Add(await RecordAsync(surface, toolkit, "only " + target, "change", hostDark, set: true));
        records.Add(await RecordAsync(surface, toolkit, other, "change", hostDark, set: true));
        Check(await region.SetAppearanceAsync(initial), $"SetAppearanceAsync did not apply the change back to {target}.");
        update = await HostLogAsync(surface, "hosted-region-update");
        Check(update["colorScheme"]?.GetValue<string>() == target, $"The host page's second update was {update.ToJsonString()}.");
        CheckAppearance(await GuestMessageAsync(surface, "appearance"), target, "more");
        records.Add(await RecordAsync(surface, toolkit, target, "change", hostDark));
        Console.WriteLine($"ENGINE-RECORD {toolkit} summary: {Summary(records, target)}; {Summary(records, "only " + target)}");

        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        await window.Closed.WaitAsync(TestTimeouts.Scale(Wait));
        Check(await application.Exited.WaitAsync(TestTimeouts.Scale(Wait)) == 0, "The guest did not exit cleanly after its window closed.");
        Console.WriteLine($"Runic region appearance smoke passed ({toolkit}).");
        return 0;
    }

    // Sets the frame's color-scheme (set) or reads the one the host's push set, then asks the guest's page for its
    // prefers-color-scheme until it matches the scheme or Settle passed, and prints what it saw.
    private static async Task<(string Scheme, string When, bool Expected, bool Seen)> RecordAsync(DesktopSurface surface, string toolkit,
        string scheme, string when, bool hostDark, bool set = false)
    {
        var quoted = JsonValue.Create(scheme).ToJsonString();
        var computed = await surface.ExecuteJavaScriptAsync(set ? $"return window.__setScheme({quoted});" : "return window.__scheme();");
        if (!set) Check(computed.Contains(scheme, StringComparison.Ordinal), $"The frame's color-scheme is '{computed}', not {scheme}.");
        var expected = scheme.EndsWith("dark", StringComparison.Ordinal);
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(Settle);
        bool seen;
        while (true)
        {
            seen = await PrefersDarkAsync(surface);
            if (seen == expected || DateTime.UtcNow > deadline) break;
            await Task.Delay(200);
        }
        // Whether the result shows anything: a frame scheme equal to the host page's own shows nothing.
        var follows = expected == hostDark ? "undetermined" : seen == expected ? "yes" : "no";
        Console.WriteLine($"ENGINE-RECORD {toolkit} color-scheme=\"{scheme}\" when={when} computed=\"{computed}\" prefers-dark={Lower(seen)} follows={follows}");
        return (scheme, when, expected, seen);
    }

    private static string Summary(List<(string Scheme, string When, bool Expected, bool Seen)> records, string scheme)
    {
        var rows = records.Where(row => row.Scheme == scheme).ToArray();
        var verdict = rows.All(static row => row.Seen == row.Expected) ? "follows" : rows.Any(static row => row.Seen == row.Expected) ? "follows sometimes" : "does not follow";
        return $"\"{scheme}\" {verdict} ({string.Join(", ", rows.Select(static row => $"{row.When} prefers-dark={Lower(row.Seen)}"))})";
    }

    private static async Task<bool> PrefersDarkAsync(DesktopSurface surface)
    {
        var id = Guid.NewGuid().ToString("N");
        await surface.ExecuteJavaScriptAsync($"window.__query({JsonValue.Create(id).ToJsonString()}); return '';");
        var answer = await GuestMessageAsync(surface, "answer", id);
        return answer["prefersDark"]!.GetValue<bool>();
    }

    // appearance().host is the push's values and appearance().colorScheme and .contrast the host's value of a member
    // that override lists (D066.6); the guest page reports both as its store would read them.
    private static void CheckAppearance(JsonObject message, string scheme, string contrast)
    {
        var host = message["host"]!;
        var combined = message["combined"]!;
        var decided = message["override"]!.AsArray().Select(static name => name!.GetValue<string>()).ToArray();
        Check(host["colorScheme"]?.GetValue<string>() == scheme && host["contrast"]?.GetValue<string>() == contrast &&
            combined["colorScheme"]?.GetValue<string>() == scheme && combined["contrast"]?.GetValue<string>() == contrast &&
            decided.Contains("colorScheme") && decided.Contains("contrast"),
            $"The guest page's appearance is {message.ToJsonString()}, not {scheme} with contrast {contrast} decided by the host.");
    }

    private static string Show(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static string Lower(bool value) => value ? "true" : "false";

    // The host page's first record of a push of this kind, removed from its log.
    private static Task<JsonObject> HostLogAsync(DesktopSurface surface, string kind) => TakeAsync(surface, "__log", kind, null,
        $"The host page received no {kind} push.");

    // The guest page's first message of this kind (and id), removed from the host page's list.
    private static Task<JsonObject> GuestMessageAsync(DesktopSurface surface, string kind, string? id = null) => TakeAsync(surface, "__guest", kind, id,
        $"The guest's page sent no '{kind}' message.");

    private static async Task<JsonObject> TakeAsync(DesktopSurface surface, string list, string kind, string? id, string failure)
    {
        var match = $"m => m && m.kind === {JsonValue.Create(kind).ToJsonString()}" + (id is null ? "" : $" && m.id === {JsonValue.Create(id).ToJsonString()}");
        var script = $"const i = window.{list}.findIndex({match}); if (i < 0) return ''; return JSON.stringify(window.{list}.splice(i, 1)[0]);";
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(Wait);
        while (DateTime.UtcNow < deadline)
        {
            var json = await surface.ExecuteJavaScriptAsync(script);
            if (json.Length > 0) return JsonNode.Parse(json)!.AsObject();
            await Task.Delay(100);
        }
        throw new TimeoutException(failure);
    }

    private static async Task WaitForAsync(DesktopSurface surface, string condition, string failure)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(Wait);
        while (DateTime.UtcNow < deadline)
        {
            if (await surface.ExecuteJavaScriptAsync($"return String({condition});") == "true") return;
            await Task.Delay(100);
        }
        throw new TimeoutException(failure);
    }

    private static RunicGuestStartInfo Guest()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The smoke has no process path.");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        arguments.AddRange([HostedSmoke.GuestArgument, "region", GuestBehaviour]);
        return new RunicGuestStartInfo(path, arguments) { WorkingDirectory = AppContext.BaseDirectory };
    }

    private static RunicHostedAppearance Appearance(string scheme) => new()
    {
        ColorScheme = scheme == "dark" ? DesktopColorScheme.Dark : DesktopColorScheme.Light,
        Contrast = DesktopContrast.More,
    };

    // Shows each window of the guest in the host page's region, with the override.
    private sealed class Policy(RunicHostedAppearance appearance) : IRunicHostedWindowPolicy
    {
        public RunicHostedWindowRequest? Request { get; private set; }

        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(RunicHostedWindowDecision.Embed(Region, new RunicHostedRegionOptions { Appearance = appearance }));
        }
    }

    // The guest: one Window, which the host shows in its region; it stays until the host closes it.
    internal static int RunGuest()
    {
        var services = new ServiceCollection();
        AddWindow(services);
        using var provider = services.BuildServiceProvider();
        try
        {
            return RunicDesktopHost.Run(provider, new DesktopHostOptions { ConnectionTimeout = TestTimeouts.Scale(Wait) },
                new RunicDesktopHostOptions
                {
                    SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html(GuestPage) },
                    Guest = RunicGuestOptions.FromStandardInput() with
                    {
                        ApplicationName = "Region appearance smoke",
                        ApplicationVersion = "1.0.0",
                        ApplicationId = "dev.runic.smoke.region-appearance",
                    },
                },
                static async host =>
                {
                    await using var window = await host.OpenWindowAsync<HostedSmokeWindow>();
                    await window.WaitForCloseAsync();
                    return 0;
                });
        }
        catch (RunicHostedApplicationException error)
        {
            Console.Error.WriteLine($"Hosted mode did not start ({error.Code}): {error.Message}");
            return 3;
        }
    }

    private static void AddWindow(IServiceCollection services)
    {
        services.AddScoped<HostedSmokeModel>();
        services.AddScoped<Func<IBridgeTransport, HostedSmokeModel, IDisposable>>(_ => (transport, model) => new HostedSmokeBridge(transport, model));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // The Views protocol on the Desktop transport, as runic-desktop-views.js sends it: a hello, then the requests.
    private const string ViewsClient = """
        const views = globalThis.__runicDesktopViews;
        const session = Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, "0")).join("");
        const request = async message => {
          globalThis.webui.allowNavigation?.(true);
          const reply = JSON.parse(await views.request("__runicViews", JSON.stringify({ ...message, session })));
          if (!reply.ok) throw new Error(`${message.kind}: ${JSON.stringify(reply)}`);
          return reply;
        };
        const hello = features => request({ kind: "hello", protocol: 4, client: { name: "runic-region-appearance-smoke", version: "1.0.0" }, features });
        const whenConnected = start => {
          let started = false;
          const once = connected => { if (connected && !started) { started = true; start().catch(error => console.error("Runic smoke", error)); } };
          views.onConnectionChange(once);
          once(views.isConnected());
        };
        """;

    // The host page: one region, which shows the frame of the content push with the CSS color-scheme of its colorScheme,
    // as <runic-hosted-region> does (a CSSOM write; null or absent clears it, an update without the member leaves it).
    private const string HostPage = $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Region appearance host</title><script src="webui.js"></script>
        <style>html,body{margin:0;height:100%} #region{position:fixed;left:0;top:40px;right:0;bottom:0} iframe{display:block;width:100%;height:100%;border:0}</style>
        </head><body><p>Region appearance host</p><div id="region"></div>
        <script>
        {{ViewsClient}}
        const region = document.getElementById("region");
        let frame = null;
        window.__log = [];
        window.__guest = [];
        window.__declared = false;
        addEventListener("message", event => { if (frame && event.source === frame.contentWindow) window.__guest.push(event.data); });
        const known = scheme => scheme === "light" || scheme === "dark" ? scheme : "";
        views.onMessage(text => {
          const push = JSON.parse(text);
          if (push.name !== "{{Region}}") return;
          if (push.kind === "hosted-region-content") {
            window.__log.push({ kind: push.kind, colorScheme: push.colorScheme ?? null });
            frame?.remove();
            frame = document.createElement("iframe");
            frame.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms");
            frame.setAttribute("allow", push.allow);
            frame.referrerPolicy = "no-referrer";
            frame.title = push.title ?? "Region guest";
            frame.style.colorScheme = known(push.colorScheme);
            region.append(frame);
            frame.src = push.src;
          } else if (push.kind === "hosted-region-update") {
            if (!("colorScheme" in push)) return;
            if (frame) frame.style.colorScheme = known(push.colorScheme);
            window.__log.push({ kind: push.kind, colorScheme: push.colorScheme });
          } else if (push.kind === "hosted-region-empty") {
            window.__log.push({ kind: push.kind, reason: push.reason });
            frame?.remove();
            frame = null;
          }
        });
        // The engine record: the frame element's color-scheme as the page set it, and its computed value.
        window.__scheme = () => `${frame.style.colorScheme} (computed ${getComputedStyle(frame).colorScheme})`;
        window.__setScheme = value => { frame.style.colorScheme = value; return window.__scheme(); };
        window.__query = id => frame.contentWindow.postMessage({ kind: "query", id }, "*");
        whenConnected(async () => {
          await hello(["hosted-regions/1"]);
          await request({ kind: "hosted-region-declare", name: "{{Region}}", sizing: "fill" });
          const box = region.getBoundingClientRect();
          await request({ kind: "hosted-region-state", name: "{{Region}}", width: Math.round(box.width), height: Math.round(box.height),
            scale: devicePixelRatio, x: Math.round(box.left), y: Math.round(box.top), visible: true, focused: false, focusSource: null });
          window.__declared = true;
        });
        </script></body></html>
        """;

    // The guest's page: it reports each appearance push with appearance().host and the combined colorScheme and contrast
    // as @runic-artifex/views reads them, and its first paint marker: the first animation frame after the hello's reply,
    // when a Views client paints its first state. It answers the host page's queries with its prefers-color-scheme.
    private const string GuestPage = $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Region appearance guest</title><script src="webui.js"></script></head>
        <body><h1>Region appearance guest</h1>
        <script>
        {{ViewsClient}}
        const say = data => parent.postMessage(data, "*");
        const dark = matchMedia("(prefers-color-scheme: dark)");
        const more = matchMedia("(prefers-contrast: more)");
        let painted = false, first = true;
        views.onMessage(text => {
          const push = JSON.parse(text);
          if (push.kind !== "appearance") return;
          const decided = Array.isArray(push.override) ? push.override : [];
          const host = { colorScheme: push.colorScheme ?? null, contrast: push.contrast ?? null };
          const combined = {
            colorScheme: decided.includes("colorScheme") && host.colorScheme ? host.colorScheme : dark.matches ? "dark" : "light",
            contrast: decided.includes("contrast") && host.contrast ? host.contrast : more.matches ? "more" : "no-preference",
          };
          say({ kind: "appearance", host, combined, override: decided, first, beforePaint: !painted });
          first = false;
        });
        addEventListener("message", event => {
          if (event.source === parent && event.data?.kind === "query") say({ kind: "answer", id: event.data.id, prefersDark: dark.matches });
        });
        whenConnected(async () => {
          await hello(["appearance/1"]);
          requestAnimationFrame(() => { painted = true; say({ kind: "painted", appearanceBefore: !first }); });
        });
        </script></body></html>
        """;

    // The host's log on standard error, as the hosted smoke writes it, guests' output included (event 2021).
    private sealed class ConsoleLogs : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Console.Error.WriteLine($"{logLevel} {category}[{eventId.Id}]: {formatter(state, exception)}{(exception is null ? "" : " " + exception)}");
        }
    }
}
