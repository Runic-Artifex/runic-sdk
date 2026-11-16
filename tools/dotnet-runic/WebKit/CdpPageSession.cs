using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Runic.Application.Tool.WebKit;

/// <summary>A CDP error answer: JSON-RPC's code and message.</summary>
internal sealed class CdpException(int code, string message) : Exception(message)
{
    internal const int MethodNotFound = -32601;
    internal const int InvalidParams = -32602;
    internal const int ServerError = -32000;

    internal int Code { get; } = code;
}

/// <summary>
/// One WebKit page behind one CDP session: translates the Chrome DevTools Protocol that JavaScript debuggers speak
/// (VS Code's js-debug, Rider) into the WebKit Inspector Protocol (WIP), and WIP events back. The commands and events
/// are the ones js-debug uses, recorded against Chromium (local-planning design/dx-0.21/WEBKIT-CDP-ADAPTER.md §2):
/// most map one to one; the browser level is the endpoint's; a few are emulated (the instrumentation pause before a
/// script with a source map runs, <c>setVariableValue</c>, <c>DOM.describeNode</c>); the rest are accepted as no-ops.
/// </summary>
internal sealed partial class CdpPageSession
{
    /// <summary>Sends a WIP command to the page and returns its result; a WIP error is a <see cref="CdpException"/>.</summary>
    internal delegate Task<JsonObject> WipSender(string method, JsonObject? parameters, CancellationToken cancellationToken);

    /// <summary>Sends a CDP event to the debugger.</summary>
    internal delegate Task CdpEmitter(string method, JsonObject parameters);

    private WipSender _wip;
    private readonly CdpEmitter _emit;
    private WebKitProtocol _protocol;
    // What the debugger set up, to set it up again on a restarted Window's page (ResumeAsync).
    private readonly object _stateGate = new();
    private readonly Dictionary<string, JsonObject> _urlBreakpoints = new(StringComparer.Ordinal);
    // Where WebKit set each of the debugger's breakpoints, as "scriptId:line:column" in WebKit's script IDs, and its
    // condition: see BreakpointsAt. A breakpoint is here from when WebKit set it until the debugger removed it.
    private readonly Dictionary<string, (string? Condition, HashSet<string> Locations)> _breakpointLocations = new(StringComparer.Ordinal);
    // The breakpoints the debugger removed lately, with a URL breakpoint's parameters: WebKit can still stop at one
    // before it reads the removal.
    private readonly Dictionary<string, JsonObject?> _removedBreakpoints = new(StringComparer.Ordinal);
    // Where the instrumentation breakpoint is in each script: its first statement ("scriptId:line:column").
    private readonly Dictionary<string, string> _instrumentationLocations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Regex> _urlRegexes = new(StringComparer.Ordinal);
    // The scripts with a source map that get instrumentation pauses, by WebKit ID. On the event loop only.
    private readonly Dictionary<string, JsonObject> _sourceMappedScripts = new(StringComparer.Ordinal);
    private readonly List<string> _blackboxPatterns = [];
    private bool _pageEnabled;
    private bool _runtimeEnabled;
    private bool _debuggerEnabled;
    private string? _pauseOnExceptions;
    private int? _asyncDepth;
    private volatile bool _suspended;
    // The CDP script IDs of a page that continues after a Window restart. JavaScriptCore numbers a new process's scripts
    // from 1 again, and js-debug takes a script ID it knows for the same script, so after each restart the IDs the
    // debugger sees carry a prefix ("r1.40"); WebKit gets them back without it.
    private string _scriptPrefix = "";
    private int _restarts;
    private readonly Channel<(string Method, JsonObject Parameters)> _events =
        Channel.CreateUnbounded<(string, JsonObject)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, string> _scriptUrls = new(StringComparer.Ordinal);
    // The emulated instrumentation pause (InstrumentationPausesAsync): the scripts with a source map that WebKit parsed
    // since its last stop, oldest first; WebKit's ID of the breakpoint that stops each script before it runs; the
    // debugger's answer to the instrumentation pause it was given last.
    private readonly Queue<JsonObject> _instrumentationScripts = new();
    private volatile bool _instrumentation;
    private volatile string? _instrumentationBreakpointId;
    private TaskCompletionSource<string>? _instrumentationAnswer;
    // An answer whose reply the debugger has not been sent yet (Replied).
    private (TaskCompletionSource<string> Answer, string Command)? _repliedAnswer;
    private volatile bool _debuggerPauseRequested;
    private bool _ignoreResumed;
    private List<JsonObject>? _replay;
    // The debugger's breakpoints that WebKit set in scripts of the replay, sent after the replayed scripts.
    private List<JsonObject>? _replayResolved;
    private int _defaultContextId;
    private string? _mainFrameId;

    internal CdpPageSession(WipSender wip, CdpEmitter emit, WebKitProtocol protocol)
    {
        _wip = wip;
        _emit = emit;
        _protocol = protocol;
    }

    /// <summary>
    /// Commands that only shape a Chromium page and have no counterpart a debugger relies on: answered with an empty
    /// result. js-debug sends them on attach.
    /// </summary>
    internal static readonly IReadOnlySet<string> AcceptedWithoutEffect = new HashSet<string>(StringComparer.Ordinal)
    {
        "Network.setCacheDisabled", "Network.disable", "Network.enable", "Network.setAttachDebugStack",
        "ServiceWorker.enable", "ServiceWorker.disable", "ServiceWorker.setForceUpdateOnPageLoad",
        "Page.waitForDebugger", "Page.setLifecycleEventsEnabled", "Page.setBypassCSP",
        "Page.addScriptToEvaluateOnNewDocument", "Page.removeScriptToEvaluateOnNewDocument",
        "Runtime.runIfWaitingForDebugger", "Runtime.setAsyncCallStackDepth", "Runtime.addBinding",
        "Profiler.enable", "Profiler.disable", "Log.enable", "Log.disable", "Target.setAutoAttach",
        "Emulation.setFocusEmulationEnabled", "Overlay.enable", "Overlay.disable", "Overlay.setShowViewportSizeOnResize",
        "Overlay.setPausedInDebuggerMessage", "Debugger.setBlackboxExecutionContexts", "Debugger.setSkipAllPauses",
        "Inspector.enable", "DOM.enable", "CSS.enable",
    };

    /// <summary>Takes a WIP event; events are translated one at a time, in WebKit's order, by <see cref="RunAsync"/>.</summary>
    internal void Post(string method, JsonObject parameters) => _events.Writer.TryWrite((method, parameters));

    /// <summary>Translates posted events until the session ends.</summary>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach ((string method, JsonObject parameters) in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await TranslateEventAsync(method, parameters).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Ends <see cref="RunAsync"/> after the events posted so far.</summary>
    internal void Complete()
    {
        _events.Writer.TryComplete();
        CancelInstrumentationAnswer();
    }

    /// <summary>
    /// The reply to a command of this page has gone to the debugger. An emulated instrumentation pause continues only
    /// then: Chromium answers <c>Debugger.resume</c> before it reports the next pause, and js-debug, which waits for that
    /// answer, would take a pause that came before it for the one it resumed from.
    /// </summary>
    internal void Replied()
    {
        (TaskCompletionSource<string> Answer, string Command)? answered;
        lock (_stateGate)
        {
            answered = _repliedAnswer;
            _repliedAnswer = null;
        }
        answered?.Answer.TrySetResult(answered.Value.Command);
    }

    private void CancelInstrumentationAnswer()
    {
        Interlocked.Exchange(ref _instrumentationAnswer, null)?.TrySetCanceled();
        lock (_stateGate)
        {
            _repliedAnswer?.Answer.TrySetCanceled();
            _repliedAnswer = null;
        }
    }

    /// <summary>
    /// The page went away and a restarted Window's page may take its place: until <see cref="ResumeAsync"/>, commands that
    /// need the page fail, and breakpoint changes are kept to set on the new page.
    /// </summary>
    internal void Suspend()
    {
        _suspended = true;
        CancelInstrumentationAnswer();
        _wip = static (method, _, _) => Task.FromException<JsonObject>(new CdpException(CdpException.ServerError, "The Window is restarting."));
    }

    /// <summary>
    /// Continues on a restarted Window's page as after a reload: the debugger forgets the old page's scripts and contexts,
    /// and gets the new ones while the domains, breakpoints and pause settings it set are set again.
    /// </summary>
    internal async Task ResumeAsync(WipSender wip, WebKitProtocol protocol)
    {
        _wip = wip;
        _protocol = protocol;
        _scriptPrefix = $"r{++_restarts}.";
        _instrumentationBreakpointId = null;
        _suspended = false;
        Post(Cleared, []);
        bool page, runtime, debugger;
        string? exceptions;
        int? depth;
        List<string> blackbox;
        List<JsonObject> breakpoints;
        lock (_stateGate)
        {
            (page, runtime, debugger, exceptions, depth) = (_pageEnabled, _runtimeEnabled, _debuggerEnabled, _pauseOnExceptions, _asyncDepth);
            blackbox = [.. _blackboxPatterns];
            breakpoints = [.. _urlBreakpoints.Values.Select(static parameters => (JsonObject)parameters.DeepClone())];
        }
        if (page)
        {
            await HandleAsync("Page.enable", []).ConfigureAwait(false);
            await HandleAsync("Page.getResourceTree", []).ConfigureAwait(false);
        }
        if (runtime) await HandleAsync("Runtime.enable", []).ConfigureAwait(false);
        if (debugger) await HandleAsync("Debugger.enable", []).ConfigureAwait(false);
        if (_instrumentation) await SetInstrumentationBreakpointAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (JsonObject parameters in breakpoints)
        {
            try
            {
                JsonObject result = await Wip("Debugger.setBreakpointByUrl", parameters).ConfigureAwait(false);
                TrackBreakpoint(result["breakpointId"]?.GetValue<string>(), parameters, result["locations"]);
            }
            catch (CdpException)
            {
            }
        }
        if (exceptions is not null) await HandleAsync("Debugger.setPauseOnExceptions", new() { ["state"] = exceptions }).ConfigureAwait(false);
        if (depth is not null) await HandleAsync("Debugger.setAsyncCallStackDepth", new() { ["maxDepth"] = depth }).ConfigureAwait(false);
        if (blackbox.Count > 0) await HandleAsync("Debugger.setBlackboxPatterns", new() { ["patterns"] = new JsonArray([.. blackbox.Select(static pattern => (JsonNode)pattern)]) }).ConfigureAwait(false);
    }

    /// <summary>A WebKit script ID as the debugger sees it.</summary>
    internal string ToCdpScriptId(string wipScriptId) => wipScriptId.Length == 0 ? "" : _scriptPrefix + wipScriptId;

    /// <summary>A script ID from the debugger as WebKit knows it; a script of an earlier Window process is gone.</summary>
    internal string ToWipScriptId(string cdpScriptId)
    {
        if (_scriptPrefix.Length == 0) return cdpScriptId.Contains('.', StringComparison.Ordinal) ? Gone() : cdpScriptId;
        return cdpScriptId.StartsWith(_scriptPrefix, StringComparison.Ordinal) ? cdpScriptId[_scriptPrefix.Length..] : Gone();
        static string Gone() => throw new CdpException(CdpException.InvalidParams, "The script belongs to a Window process that has ended.");
    }

    // A location (or a list of them) from WebKit with the debugger's script IDs.
    private JsonNode? CdpLocations(JsonNode? locations)
    {
        if (locations is null) return null;
        JsonNode copy = locations.DeepClone();
        foreach (JsonObject location in copy is JsonArray array ? array.OfType<JsonObject>() : copy is JsonObject single ? [single] : [])
        {
            if (location["scriptId"]?.GetValue<string>() is { } id) location["scriptId"] = ToCdpScriptId(id);
        }
        return copy;
    }

    // A location from the debugger with WebKit's script ID.
    private JsonObject WipLocation(JsonNode location)
    {
        var copy = (JsonObject)location.DeepClone();
        if (copy["scriptId"]?.GetValue<string>() is { } id) copy["scriptId"] = ToWipScriptId(id);
        return copy;
    }

    // Records where WebKit set one of the debugger's breakpoints (a location or a list of them, in WebKit's script IDs).
    private void TrackBreakpoint(string? id, JsonObject parameters, JsonNode? locations)
    {
        if (id is null) return;
        lock (_stateGate)
        {
            _removedBreakpoints.Remove(id);
            if (!_breakpointLocations.TryGetValue(id, out (string? Condition, HashSet<string> Locations) tracked))
            {
                tracked = (parameters["options"]?["condition"]?.GetValue<string>(), new HashSet<string>(StringComparer.Ordinal));
                _breakpointLocations[id] = tracked;
            }
            foreach (JsonObject location in locations is JsonArray array ? array.OfType<JsonObject>() : locations is JsonObject single ? [single] : [])
                tracked.Locations.Add(LocationKey(location));
        }
    }

    private static string LocationKey(JsonNode location) =>
        $"{location["scriptId"]?.GetValue<string>()}:{location["lineNumber"]?.GetValue<int>() ?? 0}:{location["columnNumber"]?.GetValue<int>() ?? 0}";

    // WebKit names a URL breakpoint after its URL (or /regex/), line and column, so the ID a suspended session gives out is
    // the one WebKit gives when the breakpoint is set again.
    internal static string UrlBreakpointId(JsonObject parameters) =>
        $"{(parameters["url"]?.GetValue<string>() is { } url ? url : "/" + parameters["urlRegex"]?.GetValue<string>() + "/")}:" +
        $"{parameters["lineNumber"]?.GetValue<int>() ?? 0}:{parameters["columnNumber"]?.GetValue<int>() ?? 0}";

    private Task<JsonObject> Wip(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default) =>
        _wip(method, parameters, cancellationToken);

    private async Task EnableAsync(string method)
    {
        if (!_protocol.HasCommand(method)) return;
        try
        {
            await Wip(method).ConfigureAwait(false);
        }
        catch (CdpException error) when (error.Message.Contains("already enabled", StringComparison.Ordinal))
        {
            // js-debug enables domains again after each navigation; WebKit answers that they already are.
        }
    }

    /// <summary>Answers one CDP command of this page.</summary>
    internal async Task<JsonObject> HandleAsync(string method, JsonObject p, CancellationToken cancellationToken = default)
    {
        switch (method)
        {
            case "Browser.getVersion":
                return CdpBrowser.Version();
            case "Page.enable":
                await EnableAsync("Page.enable").ConfigureAwait(false);
                lock (_stateGate) _pageEnabled = true;
                return [];
            case "Page.getResourceTree":
            {
                JsonObject result = await Wip("Page.getResourceTree", null, cancellationToken).ConfigureAwait(false);
                var tree = (JsonObject)(result["frameTree"]?.DeepClone() ?? new JsonObject());
                _mainFrameId = tree["frame"]?["id"]?.GetValue<string>();
                return new() { ["frameTree"] = tree };
            }
            case "Page.getFrameTree":
            {
                JsonObject result = await Wip("Page.getResourceTree", null, cancellationToken).ConfigureAwait(false);
                return new() { ["frameTree"] = WithoutResources((JsonObject)(result["frameTree"]?.DeepClone() ?? new JsonObject())) };
            }
            case "Page.reload":
                await Wip("Page.reload", new() { ["ignoreCache"] = Bool(p, "ignoreCache") }, cancellationToken).ConfigureAwait(false);
                return [];
            case "Page.navigate":
                await Wip("Page.navigate", new() { ["url"] = Required<string>(p, "url") }, cancellationToken).ConfigureAwait(false);
                return new() { ["frameId"] = _mainFrameId ?? "" };
            case "Runtime.enable":
                await EnableAsync("Runtime.enable").ConfigureAwait(false);
                await EnableAsync("Console.enable").ConfigureAwait(false);
                lock (_stateGate) _runtimeEnabled = true;
                return [];
            case "Debugger.enable":
                // WebKit replays the scripts it has when the Debugger is enabled; see TranslateEventAsync.
                Post(ReplayStart, []);
                try
                {
                    await EnableAsync("Debugger.enable").ConfigureAwait(false);
                    // WebKit's breakpoints stay inactive until this; Chromium's are active.
                    if (_protocol.HasCommand("Debugger.setBreakpointsActive"))
                        await Wip("Debugger.setBreakpointsActive", new() { ["active"] = true }, cancellationToken).ConfigureAwait(false);
                    if (_protocol.HasCommand("Debugger.setPauseOnDebuggerStatements"))
                        await Wip("Debugger.setPauseOnDebuggerStatements", new() { ["enabled"] = true }, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Post(ReplayEnd, []);
                }
                lock (_stateGate) _debuggerEnabled = true;
                return new() { ["debuggerId"] = "webkit" };
            case "Debugger.setAsyncCallStackDepth":
                lock (_stateGate) _asyncDepth = Int(p, "maxDepth");
                if (_protocol.HasCommand("Debugger.setAsyncStackTraceDepth"))
                    await Wip("Debugger.setAsyncStackTraceDepth", new() { ["depth"] = Int(p, "maxDepth") }, cancellationToken).ConfigureAwait(false);
                return [];
            case "Debugger.setInstrumentationBreakpoint":
                // WebKit cannot pause before a script with a source map runs; emulated (InstrumentationPausesAsync).
                if (p["instrumentation"]?.GetValue<string>() == BeforeScriptWithSourceMap)
                {
                    _instrumentation = true;
                    if (!_suspended) await SetInstrumentationBreakpointAsync(cancellationToken).ConfigureAwait(false);
                }
                return new() { ["breakpointId"] = InstrumentationBreakpointPrefix + p["instrumentation"]?.GetValue<string>() };
            case "Debugger.setBreakpointByUrl":
            {
                var parameters = new JsonObject { ["lineNumber"] = Int(p, "lineNumber") };
                if (p["url"] is { } url) parameters["url"] = url.GetValue<string>();
                if (p["urlRegex"] is { } regex) parameters["urlRegex"] = regex.GetValue<string>();
                if (p["columnNumber"] is { } column) parameters["columnNumber"] = column.GetValue<int>();
                if (p["condition"]?.GetValue<string>() is { Length: > 0 } condition) parameters["options"] = new JsonObject { ["condition"] = condition };
                if (_suspended)
                {
                    // Set on the restarted Window's page (ResumeAsync).
                    string pending = UrlBreakpointId(parameters);
                    lock (_stateGate) _urlBreakpoints[pending] = parameters;
                    return new() { ["breakpointId"] = pending, ["locations"] = new JsonArray() };
                }
                JsonObject result = await Wip("Debugger.setBreakpointByUrl", parameters, cancellationToken).ConfigureAwait(false);
                if (result["breakpointId"]?.GetValue<string>() is { } set) lock (_stateGate) _urlBreakpoints[set] = (JsonObject)parameters.DeepClone();
                TrackBreakpoint(result["breakpointId"]?.GetValue<string>(), parameters, result["locations"]);
                return new() { ["breakpointId"] = result["breakpointId"]?.DeepClone(), ["locations"] = CdpLocations(result["locations"]) ?? new JsonArray() };
            }
            case "Debugger.setBreakpoint":
            {
                var parameters = new JsonObject { ["location"] = WipLocation(Required<JsonNode>(p, "location")) };
                if (p["condition"]?.GetValue<string>() is { Length: > 0 } condition) parameters["options"] = new JsonObject { ["condition"] = condition };
                JsonObject result = await Wip("Debugger.setBreakpoint", parameters, cancellationToken).ConfigureAwait(false);
                TrackBreakpoint(result["breakpointId"]?.GetValue<string>(), parameters, result["actualLocation"]);
                return new() { ["breakpointId"] = result["breakpointId"]?.DeepClone(), ["actualLocation"] = CdpLocations(result["actualLocation"]) };
            }
            case "Debugger.removeBreakpoint":
            {
                string id = Required<string>(p, "breakpointId");
                if (id.StartsWith(InstrumentationBreakpointPrefix, StringComparison.Ordinal))
                {
                    _instrumentation = false;
                    if (Interlocked.Exchange(ref _instrumentationBreakpointId, null) is { } set && !_suspended)
                    {
                        try
                        {
                            await Wip("Debugger.removeBreakpoint", new() { ["breakpointId"] = set }, cancellationToken).ConfigureAwait(false);
                        }
                        catch (CdpException)
                        {
                        }
                    }
                    return [];
                }
                lock (_stateGate)
                {
                    if (_removedBreakpoints.Count >= 1024) _removedBreakpoints.Clear();
                    _removedBreakpoints[id] = _urlBreakpoints.Remove(id, out JsonObject? removed) ? removed : null;
                    _breakpointLocations.Remove(id);
                }
                if (_suspended) return [];
                try
                {
                    await Wip("Debugger.removeBreakpoint", new() { ["breakpointId"] = id }, cancellationToken).ConfigureAwait(false);
                }
                catch (CdpException)
                {
                    // Chromium does not report removing a breakpoint that a navigation already dropped either.
                }
                return [];
            }
            case "Debugger.getPossibleBreakpoints":
            {
                if (!_protocol.HasCommand("Debugger.getBreakpointLocations")) return new() { ["locations"] = new JsonArray() };
                JsonNode start = Required<JsonNode>(p, "start");
                JsonNode end = p["end"]?.DeepClone() ?? new JsonObject
                {
                    ["scriptId"] = ToWipScriptId(start["scriptId"]?.GetValue<string>() ?? ""),
                    ["lineNumber"] = (start["lineNumber"]?.GetValue<int>() ?? 0) + 1,
                    ["columnNumber"] = 0,
                };
                JsonObject result = await Wip("Debugger.getBreakpointLocations", new() { ["start"] = WipLocation(start), ["end"] = p["end"] is null ? end : WipLocation(end) }, cancellationToken).ConfigureAwait(false);
                return new() { ["locations"] = CdpLocations(result["locations"]) ?? new JsonArray() };
            }
            case "Debugger.stepOver" or "Debugger.stepInto" or "Debugger.stepOut" or "Debugger.resume":
                // The answer to an emulated instrumentation pause; WebKit stays stopped for the next one, which follows once
                // this command has its reply (Replied).
                if (Interlocked.Exchange(ref _instrumentationAnswer, null) is { } answer)
                {
                    lock (_stateGate) _repliedAnswer = (answer, method);
                    return [];
                }
                // stepInto's breakOnAsyncCall has no WebKit counterpart.
                await Wip(method, null, cancellationToken).ConfigureAwait(false);
                return [];
            case "Debugger.pause":
                if (Volatile.Read(ref _instrumentationAnswer) is not null) return [];
                _debuggerPauseRequested = true;
                await Wip(method, null, cancellationToken).ConfigureAwait(false);
                return [];
            case "Debugger.getScriptSource":
            {
                JsonObject result = await Wip("Debugger.getScriptSource", new() { ["scriptId"] = ToWipScriptId(Required<string>(p, "scriptId")) }, cancellationToken).ConfigureAwait(false);
                return new() { ["scriptSource"] = result["scriptSource"]?.DeepClone() ?? "" };
            }
            case "Debugger.setPauseOnExceptions":
            {
                // WebKit has none, uncaught and all.
                string state = Required<string>(p, "state") switch { "caught" => "all", var value => value };
                lock (_stateGate) _pauseOnExceptions = state;
                await Wip("Debugger.setPauseOnExceptions", new() { ["state"] = state }, cancellationToken).ConfigureAwait(false);
                return [];
            }
            case "Debugger.setBlackboxPatterns":
                lock (_stateGate)
                {
                    _blackboxPatterns.Clear();
                    _blackboxPatterns.AddRange((p["patterns"]?.AsArray() ?? []).Select(static pattern => pattern!.GetValue<string>()));
                }
                if (_protocol.HasCommand("Debugger.setShouldBlackboxURL"))
                {
                    foreach (JsonNode? pattern in p["patterns"]?.AsArray() ?? [])
                    {
                        await Wip("Debugger.setShouldBlackboxURL",
                            new() { ["url"] = pattern!.GetValue<string>(), ["shouldBlackbox"] = true, ["isRegex"] = true }, cancellationToken).ConfigureAwait(false);
                    }
                }
                return [];
            case "Debugger.evaluateOnCallFrame":
                return Evaluation(await Wip("Debugger.evaluateOnCallFrame", new()
                {
                    ["callFrameId"] = Required<string>(p, "callFrameId"),
                    ["expression"] = Required<string>(p, "expression"),
                    ["objectGroup"] = p["objectGroup"]?.GetValue<string>(),
                    ["includeCommandLineAPI"] = Bool(p, "includeCommandLineAPI"),
                    ["doNotPauseOnExceptionsAndMuteConsole"] = Bool(p, "silent"),
                    ["returnByValue"] = Bool(p, "returnByValue"),
                    ["generatePreview"] = Bool(p, "generatePreview"),
                }, cancellationToken).ConfigureAwait(false));
            case "Debugger.setVariableValue":
                return await SetVariableValueAsync(p, cancellationToken).ConfigureAwait(false);
            case "Runtime.evaluate":
                return await EvaluateAsync(p, cancellationToken).ConfigureAwait(false);
            case "Runtime.callFunctionOn":
            {
                string objectId = p["objectId"]?.GetValue<string>() ?? await GlobalObjectAsync(p, cancellationToken).ConfigureAwait(false);
                return Evaluation(await Wip("Runtime.callFunctionOn", new()
                {
                    ["objectId"] = objectId,
                    ["functionDeclaration"] = Required<string>(p, "functionDeclaration"),
                    ["arguments"] = p["arguments"]?.DeepClone(),
                    ["doNotPauseOnExceptionsAndMuteConsole"] = Bool(p, "silent"),
                    ["returnByValue"] = Bool(p, "returnByValue"),
                    ["generatePreview"] = Bool(p, "generatePreview"),
                    ["emulateUserGesture"] = Bool(p, "userGesture"),
                    ["awaitPromise"] = Bool(p, "awaitPromise"),
                }, cancellationToken).ConfigureAwait(false));
            }
            case "Runtime.getProperties":
                return await GetPropertiesAsync(p, cancellationToken).ConfigureAwait(false);
            case "Runtime.releaseObject":
                await Wip("Runtime.releaseObject", new() { ["objectId"] = Required<string>(p, "objectId") }, cancellationToken).ConfigureAwait(false);
                return [];
            case "Runtime.releaseObjectGroup":
                await Wip("Runtime.releaseObjectGroup", new() { ["objectGroup"] = Required<string>(p, "objectGroup") }, cancellationToken).ConfigureAwait(false);
                return [];
            case "Runtime.compileScript":
                // js-debug checks REPL input with it; WebKit compiles when it evaluates.
                return [];
            case "Runtime.globalLexicalScopeNames":
                return new() { ["names"] = new JsonArray() };
            case "DOM.describeNode":
                return await DescribeNodeAsync(p, cancellationToken).ConfigureAwait(false);
            default:
                if (AcceptedWithoutEffect.Contains(method)) return [];
                throw new CdpException(CdpException.MethodNotFound, $"'{method}' wasn't found");
        }
    }

    private async Task<JsonObject> EvaluateAsync(JsonObject p, CancellationToken cancellationToken)
    {
        bool awaitPromise = Bool(p, "awaitPromise");
        var parameters = new JsonObject
        {
            ["expression"] = Required<string>(p, "expression"),
            ["objectGroup"] = p["objectGroup"]?.GetValue<string>(),
            ["includeCommandLineAPI"] = Bool(p, "includeCommandLineAPI"),
            ["doNotPauseOnExceptionsAndMuteConsole"] = Bool(p, "silent"),
            // A promise must stay an object to be awaited.
            ["returnByValue"] = Bool(p, "returnByValue") && !awaitPromise,
            ["generatePreview"] = Bool(p, "generatePreview"),
            ["emulateUserGesture"] = Bool(p, "userGesture"),
        };
        if (p["contextId"] is { } context) parameters["contextId"] = context.GetValue<int>();
        JsonObject result = await Wip("Runtime.evaluate", parameters, cancellationToken).ConfigureAwait(false);
        if (awaitPromise && _protocol.HasCommand("Runtime.awaitPromise") && !Bool(result, "wasThrown") &&
            result["result"]?["subtype"]?.GetValue<string>() == "promise" && result["result"]?["objectId"]?.GetValue<string>() is { } promise)
        {
            result = await Wip("Runtime.awaitPromise", new()
            {
                ["promiseObjectId"] = promise,
                ["returnByValue"] = Bool(p, "returnByValue"),
                ["generatePreview"] = Bool(p, "generatePreview"),
            }, cancellationToken).ConfigureAwait(false);
        }
        return Evaluation(result);
    }

    // CDP allows an execution context instead of an object; WIP calls functions on an object: the global one.
    private async Task<string> GlobalObjectAsync(JsonObject p, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject { ["expression"] = "globalThis" };
        if (p["executionContextId"] is { } context) parameters["contextId"] = context.GetValue<int>();
        JsonObject result = await Wip("Runtime.evaluate", parameters, cancellationToken).ConfigureAwait(false);
        return result["result"]?["objectId"]?.GetValue<string>() ?? throw new CdpException(CdpException.ServerError, "The page has no global object.");
    }

    private async Task<JsonObject> GetPropertiesAsync(JsonObject p, CancellationToken cancellationToken)
    {
        JsonObject result = await Wip("Runtime.getProperties", new()
        {
            ["objectId"] = Required<string>(p, "objectId"),
            ["ownProperties"] = Bool(p, "ownProperties"),
            ["generatePreview"] = Bool(p, "generatePreview"),
        }, cancellationToken).ConfigureAwait(false);
        bool accessorsOnly = Bool(p, "accessorPropertiesOnly");
        var properties = new JsonArray();
        foreach (JsonNode? property in result["properties"]?.AsArray() ?? [])
        {
            var descriptor = (JsonObject)property!.DeepClone();
            if (accessorsOnly && descriptor["get"] is null && descriptor["set"] is null) continue;
            descriptor["configurable"] ??= false;
            descriptor["enumerable"] ??= false;
            descriptor.Remove("nativeGetter");
            descriptor.Remove("isOwn");
            properties.Add((JsonNode)descriptor);
        }
        var answer = new JsonObject { ["result"] = properties };
        if (!accessorsOnly && result["internalProperties"] is JsonArray internals) answer["internalProperties"] = internals.DeepClone();
        return answer;
    }

    // WIP has no setVariableValue: an assignment evaluated in the frame sets the nearest binding of that name, which is
    // the scope's variable unless an inner scope shadows it. Constants cannot be assigned, as in the page.
    private async Task<JsonObject> SetVariableValueAsync(JsonObject p, CancellationToken cancellationToken)
    {
        string name = Required<string>(p, "variableName");
        if (!Identifier().IsMatch(name)) throw new CdpException(CdpException.InvalidParams, "WebKit can set only variables with plain names.");
        JsonObject value = p["newValue"] as JsonObject ?? [];
        string literal = value["unserializableValue"]?.GetValue<string>() is { } raw ? raw
            : value.ContainsKey("value") ? value["value"]?.ToJsonString() ?? "null"
            : value.ContainsKey("objectId") ? throw new CdpException(CdpException.ServerError, "WebKit can set a variable only to a primitive value.")
            : "undefined";
        JsonObject result = await Wip("Debugger.evaluateOnCallFrame", new()
        {
            ["callFrameId"] = Required<string>(p, "callFrameId"),
            ["expression"] = $"{name} = {literal}",
            ["doNotPauseOnExceptionsAndMuteConsole"] = true,
        }, cancellationToken).ConfigureAwait(false);
        if (Bool(result, "wasThrown"))
            throw new CdpException(CdpException.ServerError, result["result"]?["description"]?.GetValue<string>() ?? "The assignment failed.");
        return [];
    }

    // WIP's DOM domain works on node IDs; js-debug only wants a description of an object.
    private async Task<JsonObject> DescribeNodeAsync(JsonObject p, CancellationToken cancellationToken)
    {
        JsonObject result = await Wip("Runtime.callFunctionOn", new()
        {
            ["objectId"] = Required<string>(p, "objectId"),
            ["functionDeclaration"] =
                "function(){return{nodeType:this.nodeType,nodeName:this.nodeName,localName:this.localName||'',nodeValue:this.nodeValue||''," +
                "attributes:this.attributes?Array.from(this.attributes).flatMap(a=>[a.name,a.value]):[],childNodeCount:this.childNodes?this.childNodes.length:0}}",
            ["returnByValue"] = true,
        }, cancellationToken).ConfigureAwait(false);
        if (Bool(result, "wasThrown") || result["result"]?["value"] is not JsonObject node)
            throw new CdpException(CdpException.ServerError, "The object is not a node.");
        var described = (JsonObject)node.DeepClone();
        described["nodeId"] = 0;
        described["backendNodeId"] = 0;
        return new() { ["node"] = described };
    }

    // WIP: { result, wasThrown } → CDP: { result, exceptionDetails? }.
    internal static JsonObject Evaluation(JsonObject result)
    {
        var value = (JsonObject)(result["result"]?.DeepClone() ?? new JsonObject { ["type"] = "undefined" });
        var answer = new JsonObject { ["result"] = value };
        if (Bool(result, "wasThrown"))
        {
            answer["exceptionDetails"] = new JsonObject
            {
                ["exceptionId"] = 1,
                ["text"] = "Uncaught",
                ["lineNumber"] = 0,
                ["columnNumber"] = 0,
                ["exception"] = value.DeepClone(),
            };
        }
        return answer;
    }

    // ----- events -----

    internal const string ReplayStart = "Runic.replayStart";
    internal const string Cleared = "Runic.cleared";
    internal const string ReplayEnd = "Runic.replayEnd";
    internal const string BeforeScriptWithSourceMap = "beforeScriptWithSourceMapExecution";
    internal const string InstrumentationBreakpointPrefix = "instrumentation:";
    // Every URL: WebKit matches URL breakpoints against a script's sourceURL, or its URL.
    internal const string InstrumentationUrlRegex = "^";

    /// <summary>Translates one WIP event into the CDP events js-debug expects.</summary>
    internal async Task TranslateEventAsync(string method, JsonObject p)
    {
        switch (method)
        {
            case Cleared:
                // A restarted Window's page takes over: as after a reload, the old scripts and contexts are gone.
                _scriptUrls.Clear();
                _instrumentationScripts.Clear();
                lock (_stateGate)
                {
                    _breakpointLocations.Clear();
                    _instrumentationLocations.Clear();
                }
                _sourceMappedScripts.Clear();
                _debuggerPauseRequested = false;
                _ignoreResumed = false;
                _defaultContextId = 0;
                await _emit("Runtime.executionContextsCleared", []).ConfigureAwait(false);
                return;
            case ReplayStart:
                _replay = [];
                _replayResolved = [];
                return;
            case ReplayEnd:
            {
                List<JsonObject> replay = _replay ?? [];
                List<JsonObject> resolvedInReplay = _replayResolved ?? [];
                _replay = null;
                _replayResolved = null;
                var replayed = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonObject script in CurrentScripts(replay))
                {
                    replayed.Add(script["scriptId"]?.GetValue<string>() ?? "");
                    await _emit("Debugger.scriptParsed", script).ConfigureAwait(false);
                }
                foreach (JsonObject resolved in resolvedInReplay.Where(resolved => replayed.Contains(resolved["location"]?["scriptId"]?.GetValue<string>() ?? "")))
                    await _emit("Debugger.breakpointResolved", resolved).ConfigureAwait(false);
                return;
            }
            case "Runtime.executionContextCreated":
                await ExecutionContextCreatedAsync(p).ConfigureAwait(false);
                return;
            case "Debugger.breakpointResolved":
            {
                // WebKit set a URL breakpoint in a script it parsed. Like Chromium, the debugger hears of its own: a
                // breakpoint set while a navigation replaced the script gets its location only this way, and js-debug
                // shows it unverified until then.
                JsonObject? forward = null;
                lock (_stateGate)
                {
                    if (p["breakpointId"]?.GetValue<string>() is not { } resolved || p["location"] is not JsonObject location) return;
                    if (resolved == _instrumentationBreakpointId)
                    {
                        _instrumentationLocations[location["scriptId"]?.GetValue<string>() ?? ""] = LocationKey(location);
                    }
                    else if (_breakpointLocations.TryGetValue(resolved, out (string? Condition, HashSet<string> Locations) tracked))
                    {
                        tracked.Locations.Add(LocationKey(location));
                        var cdpLocation = (JsonObject)location.DeepClone();
                        cdpLocation["scriptId"] = ToCdpScriptId(location["scriptId"]?.GetValue<string>() ?? "");
                        forward = new JsonObject { ["breakpointId"] = resolved, ["location"] = cdpLocation };
                    }
                }
                if (forward is null) return;
                // The script's scriptParsed comes first: WebKit sends it before, and a replay sends its scripts at its end.
                if (_replayResolved is not null) _replayResolved.Add(forward);
                else await _emit("Debugger.breakpointResolved", forward).ConfigureAwait(false);
                return;
            }
            case "Debugger.globalObjectCleared":
                _scriptUrls.Clear();
                _instrumentationScripts.Clear();
                lock (_stateGate)
                {
                    foreach ((string? _, HashSet<string> locations) in _breakpointLocations.Values) locations.Clear();
                    _instrumentationLocations.Clear();
                }
                _sourceMappedScripts.Clear();
                await _emit("Runtime.executionContextsCleared", []).ConfigureAwait(false);
                return;
            case "Debugger.scriptParsed":
                await ScriptParsedAsync(p).ConfigureAwait(false);
                return;
            case "Debugger.paused":
                await PausedAsync(p).ConfigureAwait(false);
                return;
            case "Debugger.resumed":
                // The adapter resumed a stop that the debugger did not see.
                if (_ignoreResumed)
                {
                    _ignoreResumed = false;
                    return;
                }
                await _emit("Debugger.resumed", []).ConfigureAwait(false);
                return;
            case "Console.messageAdded":
                await ConsoleMessageAsync(p).ConfigureAwait(false);
                return;
            case "Page.frameNavigated":
            {
                var frame = (JsonObject)(p["frame"]?.DeepClone() ?? new JsonObject());
                if (frame["parentId"] is null)
                {
                    _mainFrameId = frame["id"]?.GetValue<string>();
                    await _emit("Runtime.executionContextsCleared", []).ConfigureAwait(false);
                }
                await _emit("Page.frameNavigated", new() { ["frame"] = frame, ["type"] = "Navigation" }).ConfigureAwait(false);
                return;
            }
            case "Page.loadEventFired" or "Page.domContentEventFired":
                await _emit(method, new() { ["timestamp"] = p["timestamp"]?.DeepClone() ?? 0 }).ConfigureAwait(false);
                return;
        }
    }

    private Task ExecutionContextCreatedAsync(JsonObject p)
    {
        JsonNode context = p["context"] ?? new JsonObject();
        string type = context["type"]?.GetValue<string>() ?? "normal";
        int id = context["id"]?.GetValue<int>() ?? 0;
        string? frameId = context["frameId"]?.GetValue<string>();
        bool isDefault = type == "normal";
        if (isDefault && (_mainFrameId is null || frameId == _mainFrameId)) _defaultContextId = id;
        return _emit("Runtime.executionContextCreated", new()
        {
            ["context"] = new JsonObject
            {
                ["id"] = id,
                ["origin"] = "",
                ["name"] = context["name"]?.GetValue<string>() ?? "",
                ["uniqueId"] = $"webkit-{id}",
                ["auxData"] = new JsonObject { ["isDefault"] = isDefault, ["type"] = isDefault ? "default" : "isolated", ["frameId"] = frameId },
            },
        });
    }

    private async Task ScriptParsedAsync(JsonObject p)
    {
        string url = p["sourceURL"]?.GetValue<string>() is { Length: > 0 } sourceUrl ? sourceUrl : p["url"]?.GetValue<string>() ?? "";
        string scriptId = p["scriptId"]?.GetValue<string>() ?? "";
        _scriptUrls[scriptId] = url;
        var script = new JsonObject
        {
            ["scriptId"] = ToCdpScriptId(scriptId),
            ["url"] = url,
            ["startLine"] = p["startLine"]?.DeepClone() ?? 0,
            ["startColumn"] = p["startColumn"]?.DeepClone() ?? 0,
            ["endLine"] = p["endLine"]?.DeepClone() ?? 0,
            ["endColumn"] = p["endColumn"]?.DeepClone() ?? 0,
            ["executionContextId"] = _defaultContextId,
            ["hash"] = "",
            ["executionContextAuxData"] = new JsonObject { ["isDefault"] = true, ["type"] = "default", ["frameId"] = _mainFrameId },
            ["isModule"] = p["module"]?.GetValue<bool>() ?? false,
            ["hasSourceURL"] = p["sourceURL"] is not null,
            ["scriptLanguage"] = "JavaScript",
        };
        if (p["sourceMapURL"]?.GetValue<string>() is { Length: > 0 } map) script["sourceMapURL"] = map;
        if (_replay is not null)
        {
            _replay.Add(script);
            return;
        }
        await _emit("Debugger.scriptParsed", script).ConfigureAwait(false);
        if (_instrumentation && script["sourceMapURL"] is not null)
        {
            _instrumentationScripts.Enqueue(script);
            _sourceMappedScripts[scriptId] = script;
            // WebKit sets URL breakpoints only in scripts with a URL; for one without, the next statement has to do.
            if (url.Length == 0) _ = Wip("Debugger.pause").ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        }
    }

    // CDP's instrumentation breakpoint pauses before each script with a source map runs, so that js-debug can load the
    // map and set the breakpoints it resolves before the code runs. WebKit has no such pause. It does set a URL
    // breakpoint in each script while it parses it, before the script can run: one at the start of every script stops
    // each script before its first statement. Debugger.pause would stop only once WebKit reads the command, after the
    // script may have run.
    private async Task SetInstrumentationBreakpointAsync(CancellationToken cancellationToken)
    {
        if (_instrumentationBreakpointId is not null) return;
        try
        {
            JsonObject result = await Wip("Debugger.setBreakpointByUrl",
                new() { ["urlRegex"] = InstrumentationUrlRegex, ["lineNumber"] = 0, ["columnNumber"] = 0 }, cancellationToken).ConfigureAwait(false);
            _instrumentationBreakpointId = result["breakpointId"]?.GetValue<string>();
            lock (_stateGate)
            {
                foreach (JsonObject location in result["locations"]?.AsArray().OfType<JsonObject>() ?? [])
                    _instrumentationLocations[location["scriptId"]?.GetValue<string>() ?? ""] = LocationKey(location);
            }
        }
        catch (CdpException error) when (error.Message.Contains("already exists", StringComparison.Ordinal))
        {
            _instrumentationBreakpointId = $"/{InstrumentationUrlRegex}/:0:0";
        }
    }

    /// <summary>
    /// The replay that WebKit sends when the Debugger is enabled, as js-debug needs it. WebKit lists the scripts in hash
    /// order, including scripts of earlier page loads that are not yet collected and HMR versions that a newer one
    /// replaced; js-debug binds a source to the script it saw last. So: creation (script ID) order, and only the newest
    /// script per URL and per inline source map's list of sources.
    /// </summary>
    internal static IReadOnlyList<JsonObject> CurrentScripts(IReadOnlyList<JsonObject> replay)
    {
        static long Id(JsonObject script) =>
            long.TryParse(script["scriptId"]?.GetValue<string>()?.Split('.')[^1], out long id) ? id : long.MaxValue;
        Dictionary<string, long> newestByUrl = replay
            .Where(static script => script["url"]?.GetValue<string>() is { Length: > 0 })
            .GroupBy(static script => script["url"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Max(Id), StringComparer.Ordinal);
        Dictionary<string, long> newestBySources = replay
            .Select(static script => (script, key: InlineSourcesKey(script)))
            .Where(static entry => entry.key is not null)
            .GroupBy(static entry => entry.key!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Max(static entry => Id(entry.script)), StringComparer.Ordinal);
        var current = new List<JsonObject>();
        foreach (JsonObject script in replay.OrderBy(Id))
        {
            string url = script["url"]?.GetValue<string>() ?? "";
            if (url.Length > 0 && newestByUrl[url] != Id(script)) continue;
            if (InlineSourcesKey(script) is { } key && newestBySources[key] != Id(script)) continue;
            current.Add(script);
        }
        return current;
    }

    // The sources an inline (base64 data: URL) source map lists, as one key; null for other scripts.
    private static string? InlineSourcesKey(JsonObject script)
    {
        if (script["sourceMapURL"]?.GetValue<string>() is not { } map || !map.StartsWith("data:", StringComparison.Ordinal)) return null;
        int comma = map.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !map[..comma].EndsWith(";base64", StringComparison.Ordinal)) return null;
        try
        {
            JsonNode? json = JsonNode.Parse(Convert.FromBase64String(map[(comma + 1)..]));
            return json?["sources"] is JsonArray sources ? string.Join('\n', sources.Select(static source => source?.GetValue<string>())) : null;
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task PausedAsync(JsonObject p)
    {
        _ignoreResumed = false;
        string reason = p["reason"]?.GetValue<string>() ?? "other";
        // The adapter's own stops: the instrumentation breakpoint, and Debugger.pause for a script without a URL.
        string? attributed = reason == "Breakpoint" ? p["data"]?["breakpointId"]?.GetValue<string>() : null;
        bool adapterStop = attributed is not null
            ? attributed == _instrumentationBreakpointId
            : reason == "PauseOnNextStatement" && !_debuggerPauseRequested;
        string topScript = p["callFrames"] is JsonArray { Count: > 0 } frames ? frames[0]?["location"]?["scriptId"]?.GetValue<string>() ?? "" : "";
        JsonObject? mapped = _sourceMappedScripts.GetValueOrDefault(topScript);
        // A breakpoint stop in a script with a source map that has not had its instrumentation pause is where the script
        // starts: the instrumentation breakpoint would have stopped it there otherwise. So is one at js-debug's own
        // breakpoint at line 0. The script's instrumentation pause stands in for the stop, as with Chromium, which pauses
        // before the script runs: js-debug reports a breakpoint of its own where it is paused then.
        if (!adapterStop && attributed is not null && mapped is not null &&
            (_instrumentationScripts.Contains(mapped) || IsLineZeroBreakpoint(attributed))) adapterStop = true;
        bool scriptStart = attributed is not null && adapterStop;
        (bool ended, string? command) = await InstrumentationPausesAsync(p, scriptStart ? mapped : null).ConfigureAwait(false);
        if (ended) return;
        // A breakpoint stop: which of the debugger's breakpoints it hit, as the debugger has them now.
        string? named = adapterStop ? null : attributed;
        // Where a script with a source map starts, js-debug decides at the script's instrumentation pause whether a
        // breakpoint there stops, as it does with Chromium; reporting it again would be a second stop.
        List<(string Id, string? Condition)> candidates = attributed is null || scriptStart && mapped is not null ? [] : BreakpointsAt(p, named, scriptStart);
        JsonArray? hits = candidates.Count > 0 ? await BreakpointHitsAsync(p, candidates, named).ConfigureAwait(false) : null;
        if (hits is null && (adapterStop || attributed is not null))
        {
            // Nothing the debugger asked for: WebKit continues, or takes the step the debugger asked for last.
            _ignoreResumed = true;
            try
            {
                await Wip(command ?? "Debugger.resume").ConfigureAwait(false);
            }
            catch (CdpException)
            {
                _ignoreResumed = false;
            }
            return;
        }
        _debuggerPauseRequested = false;
        var paused = new JsonObject
        {
            ["callFrames"] = CallFrames(p),
            ["reason"] = reason switch
            {
                "exception" => "exception",
                "assert" => "assert",
                "DOM" => "DOM",
                "Listener" => "EventListener",
                "XHR" or "Fetch" or "URL" => "XHR",
                _ => "other",
            },
            ["hitBreakpoints"] = new JsonArray(),
        };
        if (hits is not null) paused["hitBreakpoints"] = hits;
        if (reason is "exception" or "assert" && p["data"] is JsonObject data) paused["data"] = data.DeepClone();
        if (p["asyncStackTrace"] is JsonObject asyncTrace) paused["asyncStackTrace"] = AsyncStackTrace(asyncTrace);
        await _emit("Debugger.paused", paused).ConfigureAwait(false);
    }

    /// <summary>
    /// The emulated instrumentation pauses while WebKit is stopped: one per script with a source map that WebKit parsed
    /// since it last stopped, oldest first, as Chromium pauses before each such script runs. js-debug loads the script's
    /// source map, sets the breakpoints it resolves and answers; then the next script's pause follows. A WebKit stop comes
    /// before the queued scripts run: the instrumentation breakpoint stops each of them at its start. The script that
    /// starts at this stop comes last, also if it had its pause at an earlier stop: js-debug reports a breakpoint that
    /// it sets where the instrumentation pause is, which WebKit would not stop at, since it already did. Returns the
    /// debugger's last answer (resume or a step), null if there was no such pause; ended if the page went away or the
    /// debugger detached meanwhile.
    /// </summary>
    private async Task<(bool Ended, string? Command)> InstrumentationPausesAsync(JsonObject p, JsonObject? starting)
    {
        List<JsonObject> scripts = [.. _instrumentationScripts.Where(script => script != starting)];
        _instrumentationScripts.Clear();
        if (!_instrumentation) return (false, null);
        if (starting is not null) scripts.Add(starting);
        string? command = null;
        foreach (JsonObject script in scripts)
        {
            var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _instrumentationAnswer, answer);
            await _emit("Debugger.paused", new()
            {
                ["callFrames"] = CallFrames(p),
                ["reason"] = "instrumentation",
                ["data"] = new JsonObject
                {
                    ["scriptId"] = script["scriptId"]!.DeepClone(),
                    ["url"] = script["url"]!.DeepClone(),
                    ["sourceMapURL"] = script["sourceMapURL"]!.DeepClone(),
                },
                ["hitBreakpoints"] = new JsonArray(),
            }).ConfigureAwait(false);
            try
            {
                command = await answer.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (true, null);
            }
            await _emit("Debugger.resumed", []).ConfigureAwait(false);
        }
        return (false, command);
    }

    /// <summary>
    /// The debugger's breakpoints at the location of a breakpoint stop, the one WebKit named first. WebKit sets one
    /// breakpoint per location, names it and evaluates no other there. At the start of a script, the instrumentation
    /// breakpoint takes that place (or js-debug's own line 0 breakpoint, which it removes at the instrumentation pause):
    /// WebKit does not set the debugger's URL breakpoints that would slide to it, and they are emulated here. Those are
    /// the ones for the script's URL at or before its first statement that WebKit did not set elsewhere in the script.
    /// </summary>
    private List<(string Id, string? Condition)> BreakpointsAt(JsonObject p, string? named, bool atScriptStart)
    {
        var found = new List<(string Id, string? Condition)>();
        lock (_stateGate)
        {
            if (named is not null && !_removedBreakpoints.ContainsKey(named)) found.Add((named, null));
            if (p["callFrames"] is not JsonArray { Count: > 0 } frames || frames[0]?["location"] is not JsonObject location) return found;
            string key = LocationKey(location);
            string scriptId = location["scriptId"]?.GetValue<string>() ?? "";
            found.AddRange(_breakpointLocations
                .Where(entry => entry.Key != named && entry.Value.Locations.Contains(key))
                .Select(static entry => (entry.Key, entry.Value.Condition)));
            // In a script with a source map, js-debug sets its breakpoints at the instrumentation pause and reports one
            // that is where the script starts.
            if (_sourceMappedScripts.ContainsKey(scriptId)) return found;
            if (!atScriptStart && _instrumentationLocations.GetValueOrDefault(scriptId) != key || !_scriptUrls.TryGetValue(scriptId, out string? url)) return found;
            int line = location["lineNumber"]?.GetValue<int>() ?? 0, column = location["columnNumber"]?.GetValue<int>() ?? 0;
            foreach ((string id, JsonObject parameters) in _urlBreakpoints)
            {
                if (id == named || found.Any(entry => entry.Id == id)) continue;
                if (_breakpointLocations.TryGetValue(id, out (string? Condition, HashSet<string> Locations) tracked) &&
                    tracked.Locations.Any(set => set.StartsWith(scriptId + ":", StringComparison.Ordinal))) continue;
                int wantedLine = parameters["lineNumber"]?.GetValue<int>() ?? 0, wantedColumn = parameters["columnNumber"]?.GetValue<int>() ?? 0;
                if (wantedLine > line || wantedLine == line && wantedColumn > column || !UrlMatches(parameters, url)) continue;
                found.Add((id, parameters["options"]?["condition"]?.GetValue<string>()));
            }
        }
        return found;
    }

    // js-debug's "entrypoint" breakpoint: a URL pattern at line 0, unconditional. It stops where a script starts, for a
    // debugger without the instrumentation pause; the user's breakpoints are at the lines of the script's source map.
    private bool IsLineZeroBreakpoint(string id)
    {
        lock (_stateGate)
        {
            return (_urlBreakpoints.TryGetValue(id, out JsonObject? parameters) || _removedBreakpoints.TryGetValue(id, out parameters)) &&
                parameters?["urlRegex"] is not null &&
                (parameters["lineNumber"]?.GetValue<int>() ?? 0) == 0 && (parameters["columnNumber"]?.GetValue<int>() ?? 0) == 0 &&
                parameters["options"]?["condition"] is null;
        }
    }

    private bool UrlMatches(JsonObject parameters, string url)
    {
        if (parameters["url"]?.GetValue<string>() is { } exact) return exact == url;
        if (parameters["urlRegex"]?.GetValue<string>() is not { } pattern) return false;
        try
        {
            if (!_urlRegexes.TryGetValue(pattern, out Regex? regex))
                _urlRegexes[pattern] = regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return regex.IsMatch(url);
        }
        catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The breakpoints a stop hit, as the debugger has them after the instrumentation pauses: those it has not removed
    /// meanwhile, whose condition holds. WebKit evaluated the named one's; the others' are evaluated as WebKit would
    /// have, and js-debug's logpoints log from theirs. Null if none.
    /// </summary>
    private async Task<JsonArray?> BreakpointHitsAsync(JsonObject p, List<(string Id, string? Condition)> breakpoints, string? named)
    {
        string? frame = p["callFrames"] is JsonArray { Count: > 0 } frames ? frames[0]?["callFrameId"]?.GetValue<string>() : null;
        var hits = new JsonArray();
        foreach ((string id, string? condition) in breakpoints)
        {
            lock (_stateGate)
            {
                if (_removedBreakpoints.ContainsKey(id)) continue;
            }
            bool hit = id == named || string.IsNullOrWhiteSpace(condition);
            if (!hit && frame is not null)
            {
                try
                {
                    JsonObject result = await Wip("Debugger.evaluateOnCallFrame", new()
                    {
                        ["callFrameId"] = frame,
                        ["expression"] = $"!!(\n{condition}\n)",
                        ["returnByValue"] = true,
                    }).ConfigureAwait(false);
                    hit = !Bool(result, "wasThrown") && result["result"]?["value"]?.GetValue<bool>() == true;
                }
                catch (Exception error) when (error is CdpException or InvalidOperationException)
                {
                }
            }
            if (hit) hits.Add((JsonNode)id);
        }
        return hits.Count > 0 ? hits : null;
    }

    private JsonArray CallFrames(JsonObject p)
    {
        var frames = new JsonArray();
        foreach (JsonNode? frame in p["callFrames"]?.AsArray() ?? [])
        {
            JsonNode location = frame!["location"] ?? new JsonObject();
            frames.Add((JsonNode)new JsonObject
            {
                ["callFrameId"] = frame["callFrameId"]?.DeepClone(),
                ["functionName"] = frame["functionName"]?.DeepClone() ?? "",
                ["location"] = CdpLocations(location),
                ["url"] = _scriptUrls.GetValueOrDefault(location["scriptId"]?.GetValue<string>() ?? "", ""),
                ["scopeChain"] = ScopeChain(frame["scopeChain"]?.AsArray() ?? []),
                ["this"] = frame["this"]?.DeepClone() ?? new JsonObject { ["type"] = "undefined" },
                ["canBeRestarted"] = false,
            });
        }
        return frames;
    }

    // WIP scope types: closure (the innermost is the function's own), functionName, nestedLexical, catch, with,
    // globalLexicalEnvironment and global.
    internal static JsonArray ScopeChain(JsonArray wip)
    {
        var scopes = new JsonArray();
        bool local = false;
        foreach (JsonNode? scope in wip)
        {
            string type = scope!["type"]?.GetValue<string>() switch
            {
                "closure" when !local => "local",
                "closure" or "functionName" => "closure",
                "nestedLexical" => "block",
                "globalLexicalEnvironment" => "script",
                var other => other ?? "closure",
            };
            local |= type == "local";
            var converted = new JsonObject { ["type"] = type, ["object"] = scope["object"]?.DeepClone() };
            if (scope["name"] is { } name) converted["name"] = name.DeepClone();
            scopes.Add((JsonNode)converted);
        }
        return scopes;
    }

    private async Task ConsoleMessageAsync(JsonObject p)
    {
        var message = (JsonObject)(p["message"]?.DeepClone() ?? new JsonObject());
        // WebKit reports evaluated code's URL as the string "undefined".
        if (message["url"]?.GetValue<string>() == "undefined") message.Remove("url");
        string? source = message["source"]?.GetValue<string>();
        string level = message["level"]?.GetValue<string>() ?? "log";
        string text = message["text"]?.GetValue<string>() ?? "";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        JsonObject? stackTrace = message["stackTrace"]?["callFrames"] is JsonArray callFrames
            ? new JsonObject { ["callFrames"] = ConsoleFrames(callFrames) }
            : message["url"] is not null
                ? new JsonObject { ["callFrames"] = new JsonArray(new JsonObject
                {
                    ["functionName"] = "",
                    ["scriptId"] = "",
                    ["url"] = message["url"]!.DeepClone(),
                    ["lineNumber"] = ZeroBased(message["line"]),
                    ["columnNumber"] = ZeroBased(message["column"]),
                }) }
                : null;
        if (source == "console-api")
        {
            string type = message["type"]?.GetValue<string>() switch
            {
                "log" or null => level switch { "warning" => "warning", "error" => "error", "debug" => "debug", "info" => "info", _ => "log" },
                var other => other,
            };
            var called = new JsonObject
            {
                ["type"] = type,
                ["args"] = message["parameters"]?.DeepClone() as JsonArray ?? new JsonArray(new JsonObject { ["type"] = "string", ["value"] = text }),
                ["executionContextId"] = _defaultContextId,
                ["timestamp"] = now,
            };
            if (stackTrace is not null) called["stackTrace"] = stackTrace;
            await _emit("Runtime.consoleAPICalled", called).ConfigureAwait(false);
        }
        else if (source == "javascript" && level == "error")
        {
            string className = text.Split(':')[0] is { Length: > 0 and < 40 } name && !name.Contains(' ', StringComparison.Ordinal) ? name : "Error";
            var details = new JsonObject
            {
                ["exceptionId"] = 1,
                ["text"] = text,
                ["lineNumber"] = ZeroBased(message["line"]),
                ["columnNumber"] = ZeroBased(message["column"]),
                ["url"] = message["url"]?.DeepClone(),
                ["executionContextId"] = _defaultContextId,
                // js-debug titles the output from the exception object, which WebKit's console message lacks.
                ["exception"] = new JsonObject { ["type"] = "object", ["subtype"] = "error", ["className"] = className, ["description"] = text },
            };
            if (stackTrace is not null) details["stackTrace"] = stackTrace;
            await _emit("Runtime.exceptionThrown", new() { ["timestamp"] = now, ["exceptionDetails"] = details }).ConfigureAwait(false);
        }
        else
        {
            await _emit("Log.entryAdded", new()
            {
                ["entry"] = new JsonObject
                {
                    ["source"] = source == "network" ? "network" : "other",
                    ["level"] = level is "warning" or "error" or "info" or "verbose" ? level : "info",
                    ["text"] = text,
                    ["timestamp"] = now,
                    ["url"] = message["url"]?.DeepClone(),
                },
            }).ConfigureAwait(false);
        }
    }

    // WIP's console and async stack frames are 1-based; CDP's are 0-based.
    private JsonArray ConsoleFrames(JsonArray wip)
    {
        var frames = new JsonArray();
        foreach (JsonNode? frame in wip)
        {
            frames.Add((JsonNode)new JsonObject
            {
                ["functionName"] = frame!["functionName"]?.DeepClone() ?? "",
                ["scriptId"] = ToCdpScriptId(frame["scriptId"]?.GetValue<string>() ?? ""),
                ["url"] = frame["url"]?.GetValue<string>() is { } url && url != "undefined" ? url : "",
                ["lineNumber"] = ZeroBased(frame["lineNumber"]),
                ["columnNumber"] = ZeroBased(frame["columnNumber"]),
            });
        }
        return frames;
    }

    private JsonObject AsyncStackTrace(JsonObject wip)
    {
        var trace = new JsonObject { ["callFrames"] = ConsoleFrames(wip["callFrames"]?.AsArray() ?? []) };
        if (wip["parentStackTrace"] is JsonObject parent) trace["parent"] = AsyncStackTrace(parent);
        return trace;
    }

    private static int ZeroBased(JsonNode? oneBased) => Math.Max(0, (oneBased?.GetValue<int>() ?? 1) - 1);

    private static JsonObject WithoutResources(JsonObject tree)
    {
        tree.Remove("resources");
        foreach (JsonNode? child in tree["childFrames"]?.AsArray() ?? []) WithoutResources((JsonObject)child!);
        return tree;
    }

    private static bool Bool(JsonObject p, string name) => p[name]?.GetValue<bool>() ?? false;

    private static int Int(JsonObject p, string name) => p[name]?.GetValue<int>() ?? 0;

    private static T Required<T>(JsonObject p, string name) where T : class =>
        (p[name] is JsonValue value && value.TryGetValue(out T? typed) ? typed : p[name] as T)
        ?? throw new CdpException(CdpException.InvalidParams, $"Missing or invalid parameter '{name}'.");

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex Identifier();
}
