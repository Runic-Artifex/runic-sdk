using System.Text;
using System.Text.Json;
using Runic.Desktop;

// SEC-06 and D040: DesktopSurfaceOptions.ContentSecurityPolicy reaches the embedded WebView (WebView2, WKWebView,
// WebKitGTK), and the Bridge works under a strict policy without 'unsafe-eval'. The page's inline script and its own
// string evaluation are blocked, while a same-origin script calls the host and receives a Views message push, which the
// served bridge script hands over as data. DesktopSurface.ExecuteJavaScriptAsync is an eval API: it fails under the policy.
internal static class ContentSecurityPolicySmoke
{
    internal const string Policy = "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    internal static DesktopContent Content() => new DesktopContent.Handler((request, _) => ValueTask.FromResult<ContentResponse?>(request.Path switch
    {
        "/" or "/index.html" => Response("text/html; charset=utf-8", """
            <!doctype html><html><head><title>csp</title><script src="webui.js"></script><script src="csp.js"></script>
            <script>document.title = "inline script ran";</script></head><body></body></html>
            """),
        "/csp.js" => Response("text/javascript; charset=utf-8", $$"""
            globalThis.__runicDesktopViews.onMessage(message => {
              const state = JSON.parse(message);
              let evaluation = "allowed";
              try { new Function("return 1")(); } catch (error) { evaluation = error?.name ?? String(error); }
              webui.call("cspReceived", JSON.stringify({ state, title: document.title, evaluation })).catch(() => {});
            });
            webui.connected.then(() => webui.call("cspReady")).catch(() => {});
            """),
        _ => null,
    }));

    internal static DesktopSurfaceOptions SurfaceOptions() => new() { Content = Content(), ContentSecurityPolicy = Policy };

    // Registers the probe's capabilities. Call it before the window opens.
    internal static Probe Register(DesktopSurface surface) => new(surface);

    internal sealed class Probe : IDisposable
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PresentationCapabilityRegistration _readyRegistration;
        private readonly PresentationCapabilityRegistration _receivedRegistration;
        private readonly DesktopSurface _surface;

        internal Probe(DesktopSurface surface)
        {
            _surface = surface;
            _readyRegistration = surface.RegisterCapability("cspReady", (_, _) =>
            {
                _ready.TrySetResult();
                return ValueTask.FromResult(PresentationResult.FromString("ok"));
            });
            _receivedRegistration = surface.RegisterCapability("cspReceived", (invocation, _) =>
            {
                _received.TrySetResult(invocation.GetString(0));
                return ValueTask.FromResult(PresentationResult.FromString("ok"));
            });
        }

        internal async Task ExerciseAsync()
        {
            using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
            await _ready.Task.WaitAsync(deadline.Token);
            // The push the Views transport (DesktopBridgeTransport) broadcasts through.
            await _surface.PushViewsMessageAsync("{\"value\":42,\"text\":\"Grüße \\u2028\"}"u8.ToArray(), deadline.Token);
            using var report = JsonDocument.Parse(await _received.Task.WaitAsync(deadline.Token));
            var root = report.RootElement;
            var title = root.GetProperty("title").GetString();
            if (title != "csp")
                throw new InvalidOperationException($"The surface's Content-Security-Policy did not block the inline script: the title is '{title}'.");
            var evaluation = root.GetProperty("evaluation").GetString();
            if (evaluation != "EvalError")
                throw new InvalidOperationException($"The policy did not block string evaluation in the page: {evaluation}.");
            var state = root.GetProperty("state");
            if (state.GetProperty("value").GetInt32() != 42 || state.GetProperty("text").GetString() != "Grüße \u2028")
                throw new InvalidOperationException($"The Bridge state push arrived changed: {state.GetRawText()}.");

            // ExecuteJavaScriptAsync evaluates its script in the page, which the policy refuses.
            try
            {
                var result = await _surface.ExecuteJavaScriptAsync("return document.title;", deadline.Token);
                throw new InvalidOperationException($"ExecuteJavaScriptAsync ran under a policy without 'unsafe-eval': '{result}'.");
            }
            catch (InvalidOperationException error) when (error.Message.StartsWith("JavaScript execution failed", StringComparison.Ordinal))
            {
            }

            Console.WriteLine("Content-Security-Policy passed: under script-src 'self' the inline script and string evaluation were " +
                "blocked, the Bridge called the host and received a state push, and ExecuteJavaScriptAsync was refused.");
        }

        public void Dispose()
        {
            _readyRegistration.Dispose();
            _receivedRegistration.Dispose();
        }
    }

    private static ContentResponse Response(string contentType, string body) =>
        new(Encoding.UTF8.GetBytes(body), contentType);
}
