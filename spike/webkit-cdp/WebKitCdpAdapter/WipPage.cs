// SPIKE (runic-sdk#1029): one WebKit page target behind one CDP session: translates CDP commands to the WebKit
// Inspector Protocol (WIP) and WIP events back to CDP. WIP's page backend multiplexes through
// Target.sendMessageToTarget / Target.dispatchMessageFromTarget to the page's inner target, which changes on a
// cross-process navigation (Target.didCommitProvisionalTarget).
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Runic.Spike.WebKitCdp;

internal sealed class WipPage
{
    private readonly InspectorSocket _socket;
    private readonly InspectorTarget _target;
    private readonly CdpConnection _cdp;
    private readonly string _sessionId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly TaskCompletionSource<string> _innerTarget = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _innerTargetId;
    private long _outerId;
    private long _innerId;
    private readonly ConcurrentDictionary<string, string> _scriptUrls = new();
    private int _defaultContextId;
    private string? _mainFrameId;
    private bool _closed;

    internal WipPage(InspectorSocket socket, InspectorTarget target, CdpConnection cdp, string sessionId)
    {
        _socket = socket;
        _target = target;
        _cdp = cdp;
        _sessionId = sessionId;
    }

    internal async Task SetupAsync()
    {
        _socket.MessageToFrontend += OnMessage;
        await _socket.SetupAsync(_target.ConnectionId, _target.TargetId);
        await _innerTarget.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The frontend handshake WebKit's own inspector sends; Inspector.initialized lets a target that waits for the
        // frontend continue.
        _ = WipAsync("Inspector.enable");
        _ = WipAsync("Inspector.initialized");
    }

    internal async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        _socket.MessageToFrontend -= OnMessage;
        await _socket.FrontendDidCloseAsync(_target.ConnectionId, _target.TargetId);
    }

    // ----- WIP transport -----

    private async Task<JsonObject> WipAsync(string method, JsonObject? @params = null)
    {
        var inner = _innerTargetId ?? await _innerTarget.Task;
        var id = Interlocked.Increment(ref _innerId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        // WIP rejects null for optional parameters: leave them out.
        foreach (var key in (@params ?? []).Where(static kv => kv.Value is null).Select(static kv => kv.Key).ToList()) @params!.Remove(key);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = @params ?? new JsonObject() }.ToJsonString();
        Log.Write("wip>", message);
        var outer = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _outerId),
            ["method"] = "Target.sendMessageToTarget",
            ["params"] = new JsonObject { ["targetId"] = inner, ["message"] = message },
        };
        await _socket.SendMessageToBackendAsync(_target.ConnectionId, _target.TargetId, outer.ToJsonString());
        var reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (reply["error"] is JsonObject error) throw new CdpError(error["code"]?.GetValue<int>() ?? -32000, error["message"]?.GetValue<string>() ?? "WebKit error");
        return reply["result"] as JsonObject ?? new JsonObject();
    }

    private void OnMessage(ulong connectionId, ulong targetId, string text)
    {
        if (connectionId != _target.ConnectionId || targetId != _target.TargetId) return;
        var m = JsonNode.Parse(text)!.AsObject();
        var method = m["method"]?.GetValue<string>();
        var p = m["params"] as JsonObject;
        switch (method)
        {
            case "Target.targetCreated" when p!["targetInfo"]!["type"]!.GetValue<string>() == "page":
                var info = p["targetInfo"]!;
                var provisional = info["isProvisional"]?.GetValue<bool>() ?? false;
                Log.Write("adapter", $"inner target {info["targetId"]} provisional={provisional} paused={info["isPaused"]}");
                if (!provisional && _innerTargetId is null) { _innerTargetId = info["targetId"]!.GetValue<string>(); _innerTarget.TrySetResult(_innerTargetId); }
                return;
            case "Target.didCommitProvisionalTarget":
                Log.Write("adapter", $"inner target {p!["oldTargetId"]} -> {p["newTargetId"]}");
                _innerTargetId = p["newTargetId"]!.GetValue<string>();
                return;
            case "Target.targetDestroyed":
                return;
            case "Target.dispatchMessageFromTarget":
                var innerText = p!["message"]!.GetValue<string>();
                Log.Write("wip<", innerText);
                var inner = JsonNode.Parse(innerText)!.AsObject();
                if (inner["id"] is { } idNode && _pending.TryRemove(idNode.GetValue<long>(), out var tcs)) tcs.TrySetResult(inner);
                else if (inner["method"]?.GetValue<string>() is { } ev) _ = TranslateEventAsync(ev, inner["params"] as JsonObject ?? new JsonObject());
                return;
            default:
                if (m["id"] is null) Log.Write("wip<", "outer " + text);
                return;
        }
    }

    // ----- CDP commands -----

    private static readonly HashSet<string> Accepted =
    [
        "Network.setCacheDisabled", "Network.disable", "Network.enable", "ServiceWorker.enable", "ServiceWorker.disable",
        "ServiceWorker.setForceUpdateOnPageLoad", "Page.waitForDebugger", "Runtime.runIfWaitingForDebugger", "Profiler.enable",
        "Profiler.disable", "Log.enable", "Log.disable", "Page.setLifecycleEventsEnabled", "Emulation.setFocusEmulationEnabled",
        "Overlay.enable", "Overlay.setShowViewportSizeOnResize", "Overlay.setPausedInDebuggerMessage", "Target.setAutoAttach",
        "Page.setBypassCSP", "Runtime.setAsyncCallStackDepth", "Debugger.setSkipAllPauses", "Runtime.addBinding",
        "Page.addScriptToEvaluateOnNewDocument", "Page.removeScriptToEvaluateOnNewDocument", "Network.setAttachDebugStack",
        "Debugger.setBlackboxExecutionContexts",
    ];

    internal async Task<JsonObject> HandleAsync(string method, JsonObject p)
    {
        switch (method)
        {
            case "Browser.getVersion":
                return new() { ["protocolVersion"] = "1.3", ["product"] = "WebKitGTK", ["revision"] = "", ["userAgent"] = "WebKitGTK", ["jsVersion"] = "" };
            case "Page.enable":
                await WipAsync("Page.enable");
                return new();
            case "Page.getResourceTree":
            {
                var r = await WipAsync("Page.getResourceTree");
                var tree = (JsonObject)r["frameTree"]!.DeepClone();
                _mainFrameId = tree["frame"]?["id"]?.GetValue<string>();
                return new() { ["frameTree"] = tree };
            }
            case "Page.getFrameTree":
            {
                var r = await WipAsync("Page.getResourceTree");
                return new() { ["frameTree"] = StripResources((JsonObject)r["frameTree"]!.DeepClone()) };
            }
            case "Page.reload":
                await WipAsync("Page.reload", new() { ["ignoreCache"] = p["ignoreCache"]?.GetValue<bool>() ?? false });
                return new();
            case "Page.navigate":
                await WipAsync("Page.navigate", new() { ["url"] = p["url"]!.GetValue<string>() });
                return new() { ["frameId"] = _mainFrameId ?? "" };
            case "Runtime.enable":
                await WipAsync("Runtime.enable");
                await WipAsync("Console.enable");
                return new();
            case "Debugger.enable":
                await WipAsync("Debugger.enable");
                await WipAsync("Debugger.setBreakpointsActive", new() { ["active"] = true });
                await WipAsync("Debugger.setPauseOnDebuggerStatements", new() { ["enabled"] = true });
                return new() { ["debuggerId"] = $"wk-{_target.TargetId}" };
            case "Debugger.setAsyncCallStackDepth":
                await WipAsync("Debugger.setAsyncStackTraceDepth", new() { ["depth"] = p["maxDepth"]?.GetValue<int>() ?? 0 });
                return new();
            case "Debugger.setInstrumentationBreakpoint":
                // GAP: WebKit has no "pause before a script with a source map runs". Breakpoints still bind through
                // the regex breakpoints js-debug sets first; code that runs during the first load may run before them.
                return new() { ["breakpointId"] = "instrumentation:" + p["instrumentation"] };
            case "Debugger.setBreakpointByUrl":
            {
                var w = new JsonObject { ["lineNumber"] = p["lineNumber"]!.GetValue<int>() };
                if (p["url"] is { } url) w["url"] = url.GetValue<string>();
                if (p["urlRegex"] is { } regex) w["urlRegex"] = regex.GetValue<string>();
                if (p["columnNumber"] is { } col) w["columnNumber"] = col.GetValue<int>();
                if (p["condition"]?.GetValue<string>() is { Length: > 0 } condition) w["options"] = new JsonObject { ["condition"] = condition };
                var r = await WipAsync("Debugger.setBreakpointByUrl", w);
                return new() { ["breakpointId"] = r["breakpointId"]!.DeepClone(), ["locations"] = r["locations"]?.DeepClone() ?? new JsonArray() };
            }
            case "Debugger.setBreakpoint":
            {
                var w = new JsonObject { ["location"] = p["location"]!.DeepClone() };
                if (p["condition"]?.GetValue<string>() is { Length: > 0 } condition) w["options"] = new JsonObject { ["condition"] = condition };
                var r = await WipAsync("Debugger.setBreakpoint", w);
                return new() { ["breakpointId"] = r["breakpointId"]!.DeepClone(), ["actualLocation"] = r["actualLocation"]!.DeepClone() };
            }
            case "Debugger.removeBreakpoint":
                if (p["breakpointId"]!.GetValue<string>().StartsWith("instrumentation:", StringComparison.Ordinal)) return new();
                try { await WipAsync("Debugger.removeBreakpoint", new() { ["breakpointId"] = p["breakpointId"]!.GetValue<string>() }); }
                catch (CdpError) { }
                return new();
            case "Debugger.getPossibleBreakpoints":
            {
                var end = p["end"]?.DeepClone() ?? new JsonObject { ["scriptId"] = p["start"]!["scriptId"]!.GetValue<string>(), ["lineNumber"] = p["start"]!["lineNumber"]!.GetValue<int>() + 1, ["columnNumber"] = 0 };
                var r = await WipAsync("Debugger.getBreakpointLocations", new() { ["start"] = p["start"]!.DeepClone(), ["end"] = end });
                return new() { ["locations"] = r["locations"]?.DeepClone() ?? new JsonArray() };
            }
            case "Debugger.stepOver" or "Debugger.stepOut" or "Debugger.resume" or "Debugger.pause":
                await WipAsync(method);
                return new();
            case "Debugger.stepInto":
                await WipAsync("Debugger.stepInto");
                return new();
            case "Debugger.getScriptSource":
            {
                var r = await WipAsync("Debugger.getScriptSource", new() { ["scriptId"] = p["scriptId"]!.GetValue<string>() });
                return new() { ["scriptSource"] = r["scriptSource"]!.DeepClone() };
            }
            case "Debugger.setPauseOnExceptions":
            {
                var state = p["state"]!.GetValue<string>() switch { "caught" => "all", var s => s };
                await WipAsync("Debugger.setPauseOnExceptions", new() { ["state"] = state });
                return new();
            }
            case "Debugger.setBlackboxPatterns":
                foreach (var pattern in p["patterns"]?.AsArray() ?? [])
                    await WipAsync("Debugger.setShouldBlackboxURL", new() { ["url"] = pattern!.GetValue<string>(), ["shouldBlackbox"] = true, ["isRegex"] = true });
                return new();
            case "Debugger.evaluateOnCallFrame":
            {
                var r = await WipAsync("Debugger.evaluateOnCallFrame", new()
                {
                    ["callFrameId"] = p["callFrameId"]!.GetValue<string>(),
                    ["expression"] = p["expression"]!.GetValue<string>(),
                    ["objectGroup"] = p["objectGroup"]?.GetValue<string>(),
                    ["includeCommandLineAPI"] = p["includeCommandLineAPI"]?.GetValue<bool>() ?? false,
                    ["doNotPauseOnExceptionsAndMuteConsole"] = p["silent"]?.GetValue<bool>() ?? false,
                    ["returnByValue"] = p["returnByValue"]?.GetValue<bool>() ?? false,
                    ["generatePreview"] = p["generatePreview"]?.GetValue<bool>() ?? false,
                });
                return EvaluationResult(r);
            }
            case "Runtime.evaluate":
            {
                var w = new JsonObject
                {
                    ["expression"] = p["expression"]!.GetValue<string>(),
                    ["objectGroup"] = p["objectGroup"]?.GetValue<string>(),
                    ["includeCommandLineAPI"] = p["includeCommandLineAPI"]?.GetValue<bool>() ?? false,
                    ["doNotPauseOnExceptionsAndMuteConsole"] = p["silent"]?.GetValue<bool>() ?? false,
                    ["returnByValue"] = (p["returnByValue"]?.GetValue<bool>() ?? false) && !(p["awaitPromise"]?.GetValue<bool>() ?? false),
                    ["generatePreview"] = p["generatePreview"]?.GetValue<bool>() ?? false,
                    ["emulateUserGesture"] = p["userGesture"]?.GetValue<bool>() ?? false,
                };
                if (p["contextId"] is { } ctx) w["contextId"] = ctx.GetValue<int>();
                var r = await WipAsync("Runtime.evaluate", w);
                if ((p["awaitPromise"]?.GetValue<bool>() ?? false) && r["result"]?["subtype"]?.GetValue<string>() == "promise" && !(r["wasThrown"]?.GetValue<bool>() ?? false))
                {
                    r = await WipAsync("Runtime.awaitPromise", new()
                    {
                        ["promiseObjectId"] = r["result"]!["objectId"]!.GetValue<string>(),
                        ["returnByValue"] = p["returnByValue"]?.GetValue<bool>() ?? false,
                        ["generatePreview"] = p["generatePreview"]?.GetValue<bool>() ?? false,
                    });
                }
                return EvaluationResult(r);
            }
            case "Runtime.callFunctionOn":
            {
                var objectId = p["objectId"]?.GetValue<string>();
                if (objectId is null)
                {
                    // CDP allows an execution context instead of an object; WIP needs an object: use globalThis.
                    var g = new JsonObject { ["expression"] = "globalThis" };
                    if (p["executionContextId"] is { } ctx) g["contextId"] = ctx.GetValue<int>();
                    objectId = (await WipAsync("Runtime.evaluate", g))["result"]!["objectId"]!.GetValue<string>();
                }
                var r = await WipAsync("Runtime.callFunctionOn", new()
                {
                    ["objectId"] = objectId,
                    ["functionDeclaration"] = p["functionDeclaration"]!.GetValue<string>(),
                    ["arguments"] = p["arguments"]?.DeepClone(),
                    ["doNotPauseOnExceptionsAndMuteConsole"] = p["silent"]?.GetValue<bool>() ?? false,
                    ["returnByValue"] = p["returnByValue"]?.GetValue<bool>() ?? false,
                    ["generatePreview"] = p["generatePreview"]?.GetValue<bool>() ?? false,
                    ["emulateUserGesture"] = p["userGesture"]?.GetValue<bool>() ?? false,
                    ["awaitPromise"] = p["awaitPromise"]?.GetValue<bool>() ?? false,
                });
                return EvaluationResult(r);
            }
            case "Runtime.getProperties":
            {
                var r = await WipAsync("Runtime.getProperties", new()
                {
                    ["objectId"] = p["objectId"]!.GetValue<string>(),
                    ["ownProperties"] = p["ownProperties"]?.GetValue<bool>() ?? false,
                    ["generatePreview"] = p["generatePreview"]?.GetValue<bool>() ?? false,
                });
                var accessorsOnly = p["accessorPropertiesOnly"]?.GetValue<bool>() ?? false;
                var props = new JsonArray();
                foreach (var prop in r["properties"]?.AsArray() ?? [])
                {
                    var d = (JsonObject)prop!.DeepClone();
                    if (accessorsOnly && d["get"] is null && d["set"] is null) continue;
                    d["configurable"] ??= false;
                    d["enumerable"] ??= false;
                    d.Remove("nativeGetter");
                    props.Add(d);
                }
                var result = new JsonObject { ["result"] = props };
                if (!accessorsOnly && r["internalProperties"] is JsonArray internals) result["internalProperties"] = internals.DeepClone();
                return result;
            }
            case "Runtime.releaseObject":
                await WipAsync("Runtime.releaseObject", new() { ["objectId"] = p["objectId"]!.GetValue<string>() });
                return new();
            case "Runtime.releaseObjectGroup":
                await WipAsync("Runtime.releaseObjectGroup", new() { ["objectGroup"] = p["objectGroup"]!.GetValue<string>() });
                return new();
            case "Runtime.compileScript":
                return new();
            case "Runtime.globalLexicalScopeNames":
                return new() { ["names"] = new JsonArray() };
            case "DOM.describeNode":
            {
                // Emulated: WIP's DOM domain works on node ids; js-debug only wants a description of an object.
                var r = await WipAsync("Runtime.callFunctionOn", new()
                {
                    ["objectId"] = p["objectId"]!.GetValue<string>(),
                    ["functionDeclaration"] = "function(){return {nodeType:this.nodeType,nodeName:this.nodeName,localName:this.localName||'',nodeValue:this.nodeValue||'',attributes:this.attributes?Array.from(this.attributes).flatMap(a=>[a.name,a.value]):[],childNodeCount:this.childNodes?this.childNodes.length:0}}",
                    ["returnByValue"] = true,
                });
                var node = (JsonObject)r["result"]!["value"]!.DeepClone();
                node["nodeId"] = 0;
                node["backendNodeId"] = 0;
                return new() { ["node"] = node };
            }
            default:
                if (Accepted.Contains(method)) return new();
                Log.Write("adapter", $"GAP: unsupported CDP method {method}");
                throw new CdpError(-32601, $"'{method}' wasn't found");
        }
    }

    private static JsonObject StripResources(JsonObject tree)
    {
        tree.Remove("resources");
        foreach (var child in tree["childFrames"]?.AsArray() ?? []) StripResources((JsonObject)child!);
        return tree;
    }

    // WIP: { result, wasThrown, savedResultIndex } -> CDP: { result, exceptionDetails? }
    private static JsonObject EvaluationResult(JsonObject r)
    {
        var result = (JsonObject)r["result"]!.DeepClone();
        var cdp = new JsonObject { ["result"] = result };
        if (r["wasThrown"]?.GetValue<bool>() ?? false)
        {
            cdp["exceptionDetails"] = new JsonObject
            {
                ["exceptionId"] = 1,
                ["text"] = "Uncaught",
                ["lineNumber"] = 0,
                ["columnNumber"] = 0,
                ["exception"] = result.DeepClone(),
            };
        }
        return cdp;
    }

    // ----- WIP events -----

    private async Task TranslateEventAsync(string method, JsonObject p)
    {
        switch (method)
        {
            case "Runtime.executionContextCreated":
            {
                var c = p["context"]!;
                var type = c["type"]?.GetValue<string>() ?? "normal";
                var id = c["id"]!.GetValue<int>();
                var frameId = c["frameId"]?.GetValue<string>();
                if (type == "normal" && (_mainFrameId is null || frameId == _mainFrameId)) _defaultContextId = id;
                await _cdp.EmitAsync(_sessionId, "Runtime.executionContextCreated", new()
                {
                    ["context"] = new JsonObject
                    {
                        ["id"] = id, ["origin"] = "", ["name"] = c["name"]?.GetValue<string>() ?? "", ["uniqueId"] = $"wk-{id}",
                        ["auxData"] = new JsonObject { ["isDefault"] = type == "normal", ["type"] = type == "normal" ? "default" : "isolated", ["frameId"] = frameId },
                    },
                });
                return;
            }
            case "Debugger.globalObjectCleared":
                _scriptUrls.Clear();
                await _cdp.EmitAsync(_sessionId, "Runtime.executionContextsCleared", new());
                return;
            case "Debugger.scriptParsed":
            {
                var url = p["sourceURL"]?.GetValue<string>() is { Length: > 0 } sourceUrl ? sourceUrl : p["url"]?.GetValue<string>() ?? "";
                var scriptId = p["scriptId"]!.GetValue<string>();
                _scriptUrls[scriptId] = url;
                var e = new JsonObject
                {
                    ["scriptId"] = scriptId, ["url"] = url,
                    ["startLine"] = p["startLine"]!.DeepClone(), ["startColumn"] = p["startColumn"]!.DeepClone(),
                    ["endLine"] = p["endLine"]!.DeepClone(), ["endColumn"] = p["endColumn"]!.DeepClone(),
                    ["executionContextId"] = _defaultContextId, ["hash"] = "",
                    ["executionContextAuxData"] = new JsonObject { ["isDefault"] = true, ["type"] = "default", ["frameId"] = _mainFrameId },
                    ["isModule"] = p["module"]?.GetValue<bool>() ?? false,
                    ["hasSourceURL"] = p["sourceURL"] is not null,
                    ["scriptLanguage"] = "JavaScript",
                };
                if (p["sourceMapURL"]?.GetValue<string>() is { Length: > 0 } map) e["sourceMapURL"] = map;
                await _cdp.EmitAsync(_sessionId, "Debugger.scriptParsed", e);
                return;
            }
            case "Debugger.paused":
            {
                var frames = new JsonArray();
                foreach (var f in p["callFrames"]!.AsArray())
                {
                    var location = f!["location"]!;
                    var scopes = new JsonArray();
                    var sawLocal = false;
                    foreach (var s in f["scopeChain"]?.AsArray() ?? [])
                    {
                        var wipType = s!["type"]!.GetValue<string>();
                        var type = wipType switch
                        {
                            "closure" when !sawLocal => "local",
                            "closure" or "functionName" => "closure",
                            "nestedLexical" => "block",
                            "globalLexicalEnvironment" => "script",
                            var other => other,
                        };
                        if (type == "local") sawLocal = true;
                        var scope = new JsonObject { ["type"] = type, ["object"] = s["object"]!.DeepClone() };
                        if (s["name"] is { } name) scope["name"] = name.DeepClone();
                        scopes.Add(scope);
                    }
                    frames.Add(new JsonObject
                    {
                        ["callFrameId"] = f["callFrameId"]!.DeepClone(),
                        ["functionName"] = f["functionName"]?.DeepClone() ?? "",
                        ["location"] = location.DeepClone(),
                        ["url"] = _scriptUrls.GetValueOrDefault(location["scriptId"]!.GetValue<string>(), ""),
                        ["scopeChain"] = scopes,
                        ["this"] = f["this"]!.DeepClone(),
                        ["canBeRestarted"] = false,
                    });
                }
                var reason = p["reason"]!.GetValue<string>();
                var e = new JsonObject
                {
                    ["callFrames"] = frames,
                    ["reason"] = reason switch { "exception" => "exception", "assert" => "assert", "DOM" => "DOM", "Listener" => "EventListener", _ => "other" },
                    ["hitBreakpoints"] = new JsonArray(),
                };
                if (reason == "Breakpoint" && p["data"]?["breakpointId"]?.GetValue<string>() is { } bp) e["hitBreakpoints"] = new JsonArray(bp);
                if (reason == "exception" && p["data"] is JsonObject data) e["data"] = data.DeepClone();
                if (p["asyncStackTrace"] is JsonObject async) e["asyncStackTrace"] = AsyncTrace(async);
                await _cdp.EmitAsync(_sessionId, "Debugger.paused", e);
                return;
            }
            case "Debugger.resumed":
                await _cdp.EmitAsync(_sessionId, "Debugger.resumed", new());
                return;
            case "Console.messageAdded":
            {
                var msg = p["message"]!;
                var source = msg["source"]?.GetValue<string>();
                var level = msg["level"]?.GetValue<string>() ?? "log";
                var stack = msg["stackTrace"]?["callFrames"] is JsonArray callFrames
                    ? new JsonObject { ["callFrames"] = Frames(callFrames) }
                    : msg["url"] is not null ? new JsonObject { ["callFrames"] = new JsonArray(new JsonObject
                    {
                        ["functionName"] = "", ["scriptId"] = "", ["url"] = msg["url"]!.DeepClone(),
                        ["lineNumber"] = Math.Max(0, (msg["line"]?.GetValue<int>() ?? 1) - 1), ["columnNumber"] = Math.Max(0, (msg["column"]?.GetValue<int>() ?? 1) - 1),
                    }) } : null;
                if (source == "console-api")
                {
                    var type = msg["type"]?.GetValue<string>() switch
                    {
                        "log" or null => level switch { "warning" => "warning", "error" => "error", "debug" => "debug", "info" => "info", _ => "log" },
                        "startGroupCollapsed" => "startGroupCollapsed", "assert" => "assert", var t => t,
                    };
                    var args = msg["parameters"]?.DeepClone() as JsonArray ?? new JsonArray(new JsonObject { ["type"] = "string", ["value"] = msg["text"]!.DeepClone() });
                    var e = new JsonObject { ["type"] = type, ["args"] = args, ["executionContextId"] = _defaultContextId, ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    if (stack is not null) e["stackTrace"] = stack;
                    await _cdp.EmitAsync(_sessionId, "Runtime.consoleAPICalled", e);
                }
                else if (source == "javascript" && level == "error")
                {
                    var details = new JsonObject
                    {
                        ["exceptionId"] = 1, ["text"] = msg["text"]!.DeepClone(),
                        ["lineNumber"] = Math.Max(0, (msg["line"]?.GetValue<int>() ?? 1) - 1), ["columnNumber"] = Math.Max(0, (msg["column"]?.GetValue<int>() ?? 1) - 1),
                        ["url"] = msg["url"]?.DeepClone(), ["executionContextId"] = _defaultContextId,
                    };
                    if (stack is not null) details["stackTrace"] = stack;
                    await _cdp.EmitAsync(_sessionId, "Runtime.exceptionThrown", new() { ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["exceptionDetails"] = details });
                }
                else
                {
                    await _cdp.EmitAsync(_sessionId, "Log.entryAdded", new()
                    {
                        ["entry"] = new JsonObject
                        {
                            ["source"] = source == "network" ? "network" : "other", ["level"] = level is "warning" or "error" or "info" or "verbose" ? level : "info",
                            ["text"] = msg["text"]!.DeepClone(), ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["url"] = msg["url"]?.DeepClone(),
                        },
                    });
                }
                return;
            }
            case "Page.frameNavigated":
            {
                var frame = (JsonObject)p["frame"]!.DeepClone();
                if (frame["parentId"] is null) { _mainFrameId = frame["id"]?.GetValue<string>(); await _cdp.EmitAsync(_sessionId, "Runtime.executionContextsCleared", new()); }
                await _cdp.EmitAsync(_sessionId, "Page.frameNavigated", new() { ["frame"] = frame, ["type"] = "Navigation" });
                return;
            }
            case "Page.loadEventFired" or "Page.domContentEventFired":
                await _cdp.EmitAsync(_sessionId, method, new() { ["timestamp"] = p["timestamp"]?.DeepClone() ?? 0 });
                return;
            default:
                return;
        }
    }

    private JsonArray Frames(JsonArray wipFrames)
    {
        var frames = new JsonArray();
        foreach (var f in wipFrames)
        {
            frames.Add(new JsonObject
            {
                ["functionName"] = f!["functionName"]?.DeepClone() ?? "",
                ["scriptId"] = f["scriptId"]?.DeepClone() ?? "",
                ["url"] = f["url"]?.DeepClone() ?? "",
                // WIP stack trace positions are 1-based; CDP's are 0-based.
                ["lineNumber"] = Math.Max(0, (f["lineNumber"]?.GetValue<int>() ?? 1) - 1),
                ["columnNumber"] = Math.Max(0, (f["columnNumber"]?.GetValue<int>() ?? 1) - 1),
            });
        }
        return frames;
    }

    private JsonObject AsyncTrace(JsonObject wip)
    {
        var cdp = new JsonObject { ["callFrames"] = Frames(wip["callFrames"]?.AsArray() ?? []) };
        if (wip["parentStackTrace"] is JsonObject parent) cdp["parent"] = AsyncTrace(parent);
        return cdp;
    }
}
