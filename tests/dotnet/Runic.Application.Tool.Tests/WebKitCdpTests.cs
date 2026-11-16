using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool;
using Runic.Application.Tool.WebKit;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

/// <summary>
/// dotnet runic dev's CDP adapter for WebKitGTK windows (#1029): WebKit's inspector socket messages, the CDP ↔ WebKit
/// Inspector Protocol translation, the endpoint's request policy, what dev opens, and an in-process round trip through a
/// fake WebKit inspector server. The js-debug scenarios against a real Window run in the webkit-cdp CI job.
/// </summary>
internal sealed partial class ToolTests
{
    [Test, DisplayName("WebKit's inspector socket: frames, target lists and frontend messages")]
    public void InspectorWireReadsAndWritesWebKitsMessages()
    {
        byte[] frame = InspectorWire.SendMessageToBackend(7, 9, "{\"id\":1}");
        Equal(InspectorWire.HeaderSize + "SendMessageToBackend".Length + 1 + 16 + 8 + 1, frame.Length);
        int size = InspectorWire.ReadBodySize(frame);
        Equal(frame.Length - InspectorWire.HeaderSize, size);
        (string name, byte[] parameters) = InspectorWire.ReadBody(frame.AsSpan(InspectorWire.HeaderSize));
        Equal("SendMessageToBackend", name);
        (ulong connection, ulong target, string message) = InspectorWire.ReadMessageToFrontend(parameters);
        Equal((7UL, 9UL, "{\"id\":1}"), (connection, target, message));
        Equal("SetupInspectorClient", InspectorWire.ReadBody(InspectorWire.SetupInspectorClient().AsSpan(InspectorWire.HeaderSize)).Name);

        // Big-endian parameters and impossible sizes are refused.
        byte[] bigEndian = (byte[])frame.Clone();
        bigEndian[4] = 0;
        Throws<InvalidOperationException>(() => InspectorWire.ReadBodySize(bigEndian));
        Throws<InvalidOperationException>(() => InspectorWire.ReadBodySize(new byte[] { 0, 0, 0, 1, 1 }));

        // Target lists of every offset size: none, one, and one larger than 255 and than 65535 bytes.
        foreach (int count in (int[])[0, 1, 3])
        {
            foreach (int urlLength in (int[])[10, 300, 70_000])
            {
                InspectorTarget[] targets = [.. Enumerable.Range(1, count).Select(index => new InspectorTarget(4, (ulong)index, "WebPage",
                    $"Page {index}", "http://127.0.0.1:1234/" + new string('a', urlLength)))];
                (ulong read, IReadOnlyList<InspectorTarget> listed) = InspectorWire.ReadTargetList(InspectorWire.WriteTargetList(4, targets));
                Equal(4UL, read);
                SequenceEqual(targets, listed);
            }
        }
        Throws<InvalidOperationException>(() => InspectorWire.ReadTargetList(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 200 }));
    }

    [Test, DisplayName("WebKit's protocol description tells which commands the adapter may send")]
    public void WebKitProtocolDetectsCommands()
    {
        WebKitProtocol protocol = WebKitProtocol.Parse("""
            InspectorBackend.registerCommand("Debugger.enable", null, [], []);
            InspectorBackend.registerCommand("Runtime.evaluate", null, [{"name": "expression", "type": "string"}], ["result"]);
            InspectorBackend.registerEvent("Debugger.paused", null, ["callFrames"]);
            """);
        True(protocol.IsKnown, "the description was read");
        True(protocol.HasCommand("Debugger.enable"), "Debugger.enable");
        False(protocol.HasCommand("Debugger.setPauseOnDebuggerStatements"), "a command the description lacks");
        True(protocol.HasEvent("Debugger.paused"), "Debugger.paused");
        // Without a description, every command is tried and WebKit answers for itself.
        WebKitProtocol unknown = WebKitProtocol.Parse("");
        False(unknown.IsKnown, "an empty description");
        True(unknown.HasCommand("Debugger.setPauseOnDebuggerStatements"), "every command without a description");
    }

    [Test, DisplayName("the CDP endpoint refuses foreign origins and host names, as Chromium does")]
    public void CdpRequestPolicyRefusesBrowsersAndRebinding()
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        foreach (string host in (string[])["127.0.0.1:9222", "localhost:9222", "LOCALHOST", "[::1]:9222", "127.0.0.1"])
            True(CdpRequestPolicy.Allows(host, null, none), host);
        foreach (string host in (string[])["example.com:9222", "evil.example", "127.0.0.1.nip.io:9222", "", "127.0.0.1:0", "127.0.0.1:x", "::1"])
            False(CdpRequestPolicy.Allows(host, null, none), host);
        False(CdpRequestPolicy.Allows(null, null, none), "no Host");
        False(CdpRequestPolicy.Allows("127.0.0.1:9222", "https://example.com", none), "a web page's Origin");
        False(CdpRequestPolicy.Allows("127.0.0.1:9222", "null", none), "an opaque Origin");
        True(CdpRequestPolicy.Allows("127.0.0.1:9222", "devtools://devtools", new HashSet<string>(["devtools://devtools"])), "an allowed Origin");

        HttpRequestHead request = HttpRequestHead.Parse("GET /devtools/browser/x HTTP/1.1\r\nHost: 127.0.0.1:9222\r\nUpgrade: websocket\r\n" +
            "Connection: keep-alive, Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n")!;
        True(request.IsWebSocketUpgrade(out string key), "an upgrade");
        Equal("dGhlIHNhbXBsZSBub25jZQ==", key);
        Equal(null, HttpRequestHead.Parse("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nHost: evil.example\r\n\r\n"));
        Equal(null, HttpRequestHead.Parse("GET / HTTP/1.1\r\nOrigin: a\r\norigin: b\r\n\r\n"));
        Equal(null, HttpRequestHead.Parse("GARBAGE\r\n\r\n"));
    }

    // A fake WebKit page: records the WIP commands and answers them from a table.
    private sealed class FakeWebKit
    {
        internal List<(string Method, JsonObject Parameters)> Sent { get; } = [];
        internal List<(string Method, JsonObject Parameters)> Emitted { get; } = [];
        internal Func<string, JsonObject, JsonObject> Answer { get; set; } = static (_, _) => [];

        internal Task<JsonObject> SendAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
        {
            var copy = (JsonObject)(parameters?.DeepClone() ?? new JsonObject());
            lock (Sent) Sent.Add((method, copy));
            try
            {
                return Task.FromResult(Answer(method, copy));
            }
            catch (CdpException error)
            {
                return Task.FromException<JsonObject>(error);
            }
        }

        internal Task EmitAsync(string method, JsonObject parameters)
        {
            lock (Emitted) Emitted.Add((method, parameters));
            return Task.CompletedTask;
        }

        internal CdpPageSession Session(WebKitProtocol? protocol = null) => new(SendAsync, EmitAsync, protocol ?? WebKitProtocol.Unknown);
    }

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    [Test, DisplayName("CDP commands become the WebKit Inspector Protocol commands js-debug needs")]
    public async Task CdpCommandsTranslateToWebKit()
    {
        var webKit = new FakeWebKit();
        CdpPageSession session = webKit.Session();

        // Breakpoints: conditions move into options; WebKit's answer passes through.
        webKit.Answer = (method, _) => method == "Debugger.setBreakpointByUrl"
            ? Json("""{"breakpointId":"http://h/a.js:4:2","locations":[{"scriptId":"7","lineNumber":5,"columnNumber":1}]}""")
            : [];
        JsonObject breakpoint = await session.HandleAsync("Debugger.setBreakpointByUrl",
            Json("""{"url":"http://h/a.js","lineNumber":4,"columnNumber":2,"condition":"x > 1"}"""));
        Equal("http://h/a.js:4:2", breakpoint["breakpointId"]!.GetValue<string>());
        Equal("7", breakpoint["locations"]![0]!["scriptId"]!.GetValue<string>());
        JsonObject sentBreakpoint = webKit.Sent[^1].Parameters;
        Equal(("http://h/a.js", 4, 2, "x > 1"), (sentBreakpoint["url"]!.GetValue<string>(), sentBreakpoint["lineNumber"]!.GetValue<int>(),
            sentBreakpoint["columnNumber"]!.GetValue<int>(), sentBreakpoint["options"]!["condition"]!.GetValue<string>()));

        // WebKit pauses on all or uncaught exceptions; CDP's caught means all.
        await session.HandleAsync("Debugger.setPauseOnExceptions", Json("""{"state":"caught"}"""));
        Equal("all", webKit.Sent[^1].Parameters["state"]!.GetValue<string>());

        // Debugger.enable activates WebKit's breakpoints and debugger statements; a domain WebKit already enabled is fine.
        webKit.Sent.Clear();
        webKit.Answer = (method, _) => method == "Debugger.enable" ? throw new CdpException(-32000, "Debugger domain already enabled") : [];
        JsonObject enabled = await session.HandleAsync("Debugger.enable", []);
        Equal("webkit", enabled["debuggerId"]!.GetValue<string>());
        SequenceEqual(["Debugger.enable", "Debugger.setBreakpointsActive", "Debugger.setPauseOnDebuggerStatements"],
            [.. webKit.Sent.Select(static sent => sent.Method)]);

        // A thrown evaluation is CDP's exceptionDetails; awaitPromise awaits WebKit's promise object.
        webKit.Answer = (method, _) => method switch
        {
            "Runtime.evaluate" => Json("""{"result":{"type":"object","subtype":"promise","objectId":"p1"},"wasThrown":false}"""),
            "Runtime.awaitPromise" => Json("""{"result":{"type":"object","subtype":"error","description":"Error: no"},"wasThrown":true}"""),
            _ => [],
        };
        JsonObject evaluated = await session.HandleAsync("Runtime.evaluate", Json("""{"expression":"f()","awaitPromise":true,"returnByValue":true,"silent":true}"""));
        Equal("Error: no", evaluated["exceptionDetails"]!["exception"]!["description"]!.GetValue<string>());
        JsonObject evaluate = webKit.Sent.Last(static sent => sent.Method == "Runtime.evaluate").Parameters;
        Equal(false, evaluate["returnByValue"]!.GetValue<bool>());
        Equal(true, evaluate["doNotPauseOnExceptionsAndMuteConsole"]!.GetValue<bool>());
        Equal("p1", webKit.Sent[^1].Parameters["promiseObjectId"]!.GetValue<string>());

        // callFunctionOn without an object calls on the global object.
        webKit.Sent.Clear();
        webKit.Answer = (method, _) => method == "Runtime.evaluate"
            ? Json("""{"result":{"type":"object","objectId":"global"}}""")
            : Json("""{"result":{"type":"number","value":3}}""");
        JsonObject called = await session.HandleAsync("Runtime.callFunctionOn", Json("""{"functionDeclaration":"function(){return 3}","executionContextId":2}"""));
        Equal(3, called["result"]!["value"]!.GetValue<int>());
        Equal("globalThis", webKit.Sent[0].Parameters["expression"]!.GetValue<string>());
        Equal(2, webKit.Sent[0].Parameters["contextId"]!.GetValue<int>());
        Equal("global", webKit.Sent[1].Parameters["objectId"]!.GetValue<string>());

        // getProperties: WebKit's properties are CDP's result; accessorPropertiesOnly keeps the accessors.
        webKit.Answer = (_, _) => Json("""{"properties":[{"name":"a","value":{"type":"number","value":1},"isOwn":true},{"name":"b","get":{"type":"function"}}],"internalProperties":[{"name":"[[Target]]"}]}""");
        JsonObject properties = await session.HandleAsync("Runtime.getProperties", Json("""{"objectId":"o","ownProperties":true}"""));
        Equal(2, properties["result"]!.AsArray().Count);
        Equal(false, properties["result"]![0]!.AsObject().ContainsKey("isOwn"));
        Equal(1, properties["internalProperties"]!.AsArray().Count);
        JsonObject accessors = await session.HandleAsync("Runtime.getProperties", Json("""{"objectId":"o","accessorPropertiesOnly":true}"""));
        Equal("b", accessors["result"]!.AsArray().Single()!["name"]!.GetValue<string>());

        // setVariableValue: an assignment in the frame; only plain names.
        webKit.Answer = (_, _) => Json("""{"result":{"type":"number","value":42}}""");
        await session.HandleAsync("Debugger.setVariableValue", Json("""{"callFrameId":"f","scopeNumber":0,"variableName":"count","newValue":{"value":42}}"""));
        Equal("count = 42", webKit.Sent[^1].Parameters["expression"]!.GetValue<string>());
        CdpException invalid = await ThrowsAsync<CdpException>(() => session.HandleAsync("Debugger.setVariableValue",
            Json("""{"callFrameId":"f","variableName":"a;b","newValue":{"value":1}}""")));
        Equal(CdpException.InvalidParams, invalid.Code);

        // Commands without a WebKit counterpart: accepted no-ops, or JSON-RPC's method not found.
        webKit.Sent.Clear();
        Equal(0, (await session.HandleAsync("Network.setCacheDisabled", Json("""{"cacheDisabled":true}"""))).Count);
        Equal(0, webKit.Sent.Count);
        Equal(CdpException.MethodNotFound, (await ThrowsAsync<CdpException>(() => session.HandleAsync("Storage.clearDataForOrigin", []))).Code);
    }

    [Test, DisplayName("commands WebKit's protocol description lacks are not sent")]
    public async Task CdpSessionDetectsWebKitFeatures()
    {
        var webKit = new FakeWebKit();
        CdpPageSession session = webKit.Session(WebKitProtocol.Parse("""
            InspectorBackend.registerCommand("Debugger.enable", null, [], []);
            InspectorBackend.registerCommand("Debugger.setBreakpointsActive", null, [], []);
            """));
        await session.HandleAsync("Debugger.enable", []);
        await session.HandleAsync("Debugger.setAsyncCallStackDepth", Json("""{"maxDepth":32}"""));
        await session.HandleAsync("Debugger.setBlackboxPatterns", Json("""{"patterns":["node_modules"]}"""));
        Equal(0, (await session.HandleAsync("Debugger.getPossibleBreakpoints", Json("""{"start":{"scriptId":"1","lineNumber":0}}""")))["locations"]!.AsArray().Count);
        SequenceEqual(["Debugger.enable", "Debugger.setBreakpointsActive"], [.. webKit.Sent.Select(static sent => sent.Method)]);
    }

    [Test, DisplayName("WebKit's events become the CDP events js-debug expects")]
    public async Task WebKitEventsTranslateToCdp()
    {
        var webKit = new FakeWebKit();
        CdpPageSession session = webKit.Session();
        await session.TranslateEventAsync("Runtime.executionContextCreated", Json("""{"context":{"id":5,"type":"normal","name":"","frameId":"0.1"}}"""));
        JsonObject context = webKit.Emitted[^1].Parameters["context"]!.AsObject();
        Equal(true, context["auxData"]!["isDefault"]!.GetValue<bool>());
        Equal("0.1", context["auxData"]!["frameId"]!.GetValue<string>());

        await session.TranslateEventAsync("Debugger.scriptParsed",
            Json("""{"scriptId":"9","url":"http://h/src/App.tsx","startLine":0,"startColumn":0,"endLine":40,"endColumn":0,"sourceMapURL":"data:x","module":true}"""));
        JsonObject script = webKit.Emitted[^1].Parameters;
        Equal(("Debugger.scriptParsed", 5, true, "data:x"),
            (webKit.Emitted[^1].Method, script["executionContextId"]!.GetValue<int>(), script["isModule"]!.GetValue<bool>(), script["sourceMapURL"]!.GetValue<string>()));

        await session.TranslateEventAsync("Debugger.paused", Json("""
            {"reason":"Breakpoint","data":{"breakpointId":"bp1"},"callFrames":[{"callFrameId":"c0","functionName":"onClick",
              "location":{"scriptId":"9","lineNumber":30,"columnNumber":4},"this":{"type":"undefined"},
              "scopeChain":[{"type":"closure","object":{"objectId":"s0"},"name":"onClick"},{"type":"nestedLexical","object":{"objectId":"s1"}},
                {"type":"closure","object":{"objectId":"s2"}},{"type":"globalLexicalEnvironment","object":{"objectId":"s3"}},{"type":"global","object":{"objectId":"s4"}}]}]}
            """));
        JsonObject paused = webKit.Emitted[^1].Parameters;
        Equal("other", paused["reason"]!.GetValue<string>());
        Equal("bp1", paused["hitBreakpoints"]![0]!.GetValue<string>());
        JsonObject frame = paused["callFrames"]![0]!.AsObject();
        Equal("http://h/src/App.tsx", frame["url"]!.GetValue<string>());
        SequenceEqual(["local", "block", "closure", "script", "global"],
            [.. frame["scopeChain"]!.AsArray().Select(static scope => scope!["type"]!.GetValue<string>())]);

        await session.TranslateEventAsync("Console.messageAdded", Json("""{"message":{"source":"console-api","level":"warning","type":"log","text":"w","parameters":[{"type":"string","value":"w"}],"url":"http://h/a.js","line":3,"column":7}}"""));
        JsonObject console = webKit.Emitted[^1].Parameters;
        Equal(("Runtime.consoleAPICalled", "warning", 2, 6), (webKit.Emitted[^1].Method, console["type"]!.GetValue<string>(),
            console["stackTrace"]!["callFrames"]![0]!["lineNumber"]!.GetValue<int>(), console["stackTrace"]!["callFrames"]![0]!["columnNumber"]!.GetValue<int>()));

        await session.TranslateEventAsync("Console.messageAdded", Json("""{"message":{"source":"javascript","level":"error","text":"TypeError: x is undefined","url":"undefined","line":1,"column":2}}"""));
        JsonObject thrown = webKit.Emitted[^1].Parameters["exceptionDetails"]!.AsObject();
        Equal(("Runtime.exceptionThrown", "TypeError", true), (webKit.Emitted[^1].Method,
            thrown["exception"]!["className"]!.GetValue<string>(), thrown["url"] is null));

        await session.TranslateEventAsync("Debugger.globalObjectCleared", []);
        Equal("Runtime.executionContextsCleared", webKit.Emitted[^1].Method);
    }

    [Test, DisplayName("the replay on attach is in creation order, without stale or superseded scripts")]
    public void ReplayKeepsTheCurrentScripts()
    {
        static string Map(params string[] sources) =>
            "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                new JsonObject { ["sources"] = new JsonArray([.. sources.Select(static source => (JsonNode)source)]) }.ToJsonString()));
        static JsonObject Script(string id, string url, string? map = null)
        {
            var script = new JsonObject { ["scriptId"] = id, ["url"] = url };
            if (map is not null) script["sourceMapURL"] = map;
            return script;
        }
        // WebKit's hash order; an earlier page load's App.tsx (12), and an HMR edit (20) that a later edit (31) replaced.
        IReadOnlyList<JsonObject> current = CdpPageSession.CurrentScripts([
            Script("31", "http://h/src/App.tsx?t=2", Map("App.tsx")),
            Script("7", "http://h/src/main.tsx", Map("main.tsx")),
            Script("12", "http://h/src/App.tsx?t=2", Map("App.tsx")),
            Script("20", "http://h/src/App.tsx?t=1", Map("App.tsx")),
            Script("3", "user-script:1"),
            Script("4", ""),
            Script("5", ""),
        ]);
        SequenceEqual(["3", "4", "5", "7", "31"], [.. current.Select(static script => script["scriptId"]!.GetValue<string>())]);
    }

    [Test, DisplayName("the source-map pause is emulated: js-debug sets its breakpoints before the script runs on")]
    public async Task InstrumentationPauseIsEmulated()
    {
        var webKit = new FakeWebKit();
        CdpPageSession session = webKit.Session();
        JsonObject instrumentation = await session.HandleAsync("Debugger.setInstrumentationBreakpoint", Json("""{"instrumentation":"beforeScriptWithSourceMapExecution"}"""));
        Equal("instrumentation:beforeScriptWithSourceMapExecution", instrumentation["breakpointId"]!.GetValue<string>());
        await session.TranslateEventAsync("Debugger.scriptParsed", Json("""{"scriptId":"1","url":"http://h/a.js","sourceMapURL":"a.js.map"}"""));
        await session.TranslateEventAsync("Debugger.scriptParsed", Json("""{"scriptId":"2","url":"http://h/b.js","sourceMapURL":"b.js.map"}"""));
        await Task.Delay(50);
        Equal(1, webKit.Sent.Count(static sent => sent.Method == "Debugger.pause"));
        await session.TranslateEventAsync("Debugger.paused", Json("""{"reason":"PauseOnNextStatement","callFrames":[]}"""));
        JsonObject paused = webKit.Emitted[^1].Parameters;
        Equal(("instrumentation", "2", "b.js.map"), (paused["reason"]!.GetValue<string>(), paused["data"]!["scriptId"]!.GetValue<string>(),
            paused["data"]!["sourceMapURL"]!.GetValue<string>()));
        // Removing the instrumentation breakpoint ends the emulation; nothing reaches WebKit.
        int sent = webKit.Sent.Count;
        await session.HandleAsync("Debugger.removeBreakpoint", Json("""{"breakpointId":"instrumentation:beforeScriptWithSourceMapExecution"}"""));
        await session.TranslateEventAsync("Debugger.scriptParsed", Json("""{"scriptId":"3","url":"http://h/c.js","sourceMapURL":"c.js.map"}"""));
        await Task.Delay(50);
        Equal(sent, webKit.Sent.Count);
    }

    [Test, DisplayName("after a Window restart the session continues on the new page with its breakpoints")]
    public async Task SuspendedSessionResumesOnARestartedWindow()
    {
        var webKit = new FakeWebKit();
        CdpPageSession session = webKit.Session();
        await session.HandleAsync("Runtime.enable", []);
        await session.HandleAsync("Debugger.enable", []);
        webKit.Answer = (method, parameters) => method == "Debugger.setBreakpointByUrl"
            ? new JsonObject { ["breakpointId"] = CdpPageSession.UrlBreakpointId(parameters), ["locations"] = new JsonArray() }
            : [];
        await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"url":"http://h/a.js","lineNumber":1,"columnNumber":0}"""));
        await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"urlRegex":"a\\.js$","lineNumber":9}"""));
        await session.HandleAsync("Debugger.removeBreakpoint", Json("""{"breakpointId":"http://h/a.js:1:0"}"""));

        session.Suspend();
        Equal(CdpException.ServerError, (await ThrowsAsync<CdpException>(() => session.HandleAsync("Runtime.evaluate", Json("""{"expression":"1"}""")))).Code);
        // While the Window restarts, breakpoint changes are kept, under the IDs WebKit gives them.
        JsonObject pending = await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"url":"http://h/b.js","lineNumber":2,"columnNumber":4}"""));
        Equal("http://h/b.js:2:4", pending["breakpointId"]!.GetValue<string>());

        var restarted = new FakeWebKit { Answer = webKit.Answer };
        Task events = Task.Run(() => session.RunAsync(CancellationToken.None));
        await session.ResumeAsync(restarted.SendAsync, WebKitProtocol.Unknown);
        string[] methods = [.. restarted.Sent.Select(static sent => sent.Method)];
        SequenceEqual(["Runtime.enable", "Console.enable", "Debugger.enable", "Debugger.setBreakpointsActive", "Debugger.setPauseOnDebuggerStatements"],
            methods[..5]);
        SequenceEqual(["/a\\.js$/:9:0", "http://h/b.js:2:4"],
            [.. restarted.Sent.Where(static sent => sent.Method == "Debugger.setBreakpointByUrl")
                .Select(static sent => CdpPageSession.UrlBreakpointId(sent.Parameters)).Order(StringComparer.Ordinal)]);
        session.Complete();
        await events;
        Equal("Runtime.executionContextsCleared", webKit.Emitted.Select(static emitted => emitted.Method).First(static method => method.StartsWith("Runtime.", StringComparison.Ordinal)));

        // The new process numbers its scripts from 1 again: the debugger sees them prefixed, WebKit gets them back plain,
        // and a script of the ended process is refused.
        await session.TranslateEventAsync("Debugger.scriptParsed", Json("""{"scriptId":"40","url":"http://h/src/App.tsx?t=2","sourceMapURL":"data:x"}"""));
        Equal("r1.40", webKit.Emitted[^1].Parameters["scriptId"]!.GetValue<string>());
        await session.TranslateEventAsync("Debugger.paused", Json("""{"reason":"Breakpoint","callFrames":[{"callFrameId":"c","location":{"scriptId":"40","lineNumber":3}}]}"""));
        Equal("r1.40", webKit.Emitted[^1].Parameters["callFrames"]![0]!["location"]!["scriptId"]!.GetValue<string>());
        restarted.Answer = static (_, _) => Json("""{"scriptSource":"code"}""");
        await session.HandleAsync("Debugger.getScriptSource", Json("""{"scriptId":"r1.40"}"""));
        Equal("40", restarted.Sent[^1].Parameters["scriptId"]!.GetValue<string>());
        Equal(CdpException.InvalidParams, (await ThrowsAsync<CdpException>(() => session.HandleAsync("Debugger.getScriptSource", Json("""{"scriptId":"40"}""")))).Code);
    }

    [Test, DisplayName("dev opens WebKit's inspector server and the CDP adapter on Linux, and honours the developer's choice")]
    public void WebKitDebuggingPlans()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration desktop = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["desktop"] };
        var options = new DevOptions(null, "Debug", true, true, true, false, []);
        int port = 40000;
        WebKitDebugging.Plan Decide(DevOptions with, Func<string, string?>? environment = null, bool linux = true,
            DevProjectConfiguration? configuration = null, Version? version = null) =>
            WebKitDebugging.Decide(configuration ?? desktop, with, environment ?? (static _ => null), linux, () => port++, () => version);

        WebKitDebugging.Plan defaults = Decide(options);
        True(defaults.StartAdapter, "the adapter by default");
        Equal(("127.0.0.1", 40000), (defaults.InspectorHost, defaults.InspectorPort));
        SequenceEqual([new KeyValuePair<string, string>("WEBKIT_INSPECTOR_SERVER", "127.0.0.1:40000")], defaults.WindowEnvironment);
        Equal(new DevSession.DebugEndpoint("webkitgtk", "webkit-remote-inspector", "inspector://127.0.0.1:40000"), defaults.Endpoints.Single());
        // --no-window: the IDE starts the Window with the template's fixed address.
        WebKitDebugging.Plan noWindow = Decide(options with { StartWindow = false });
        Equal(("127.0.0.1", 9223, "127.0.0.1:9223"), (noWindow.InspectorHost, noWindow.InspectorPort, noWindow.WindowEnvironment.Single().Value));

        // Nothing off Linux, for CS-WebUI, or with --no-debug-endpoint.
        Equal(WebKitDebugging.Plan.None, Decide(options, linux: false));
        Equal(WebKitDebugging.Plan.None, Decide(options, configuration: desktop with { ApplicationHosts = ["cswebui"] }));
        Equal(WebKitDebugging.Plan.None, Decide(options with { DebugEndpoint = false }));

        // A WEBKIT_INSPECTOR_SERVER of the developer's (-e wins over dev's environment) is used, if it is loopback.
        WebKitDebugging.Plan chosen = Decide(options with { Environment = [new("WEBKIT_INSPECTOR_SERVER", "127.0.0.1:5555")] },
            name => name == "WEBKIT_INSPECTOR_SERVER" ? "127.0.0.1:6666" : null);
        Equal((true, "127.0.0.1", 5555, 0), (chosen.StartAdapter, chosen.InspectorHost, chosen.InspectorPort, chosen.WindowEnvironment.Count));
        False(Decide(options, name => name == "WEBKIT_INSPECTOR_SERVER" ? "0.0.0.0:5555" : null).StartAdapter, "a non-loopback address");
        // WEBKIT_INSPECTOR_HTTP_SERVER replaces the socket server in WebKit, so no adapter.
        WebKitDebugging.Plan http = Decide(options, name => name == "WEBKIT_INSPECTOR_HTTP_SERVER" ? "127.0.0.1:7777" : null);
        Equal((false, "webkit-inspector"), (http.StartAdapter, http.Endpoints.Single().Protocol));
        Contains(http.Hints.Single(), "ignores WEBKIT_INSPECTOR_SERVER");

        // --webkit-inspector: WebKit's HTTP inspector only where WebKitGTK refuses other origins (bug 330602).
        WebKitDebugging.Plan fixedVersion = Decide(options with { WebKitInspector = true }, version: new Version(2, 54, 3));
        False(fixedVersion.StartAdapter, "no adapter beside the HTTP inspector");
        Equal("WEBKIT_INSPECTOR_HTTP_SERVER", fixedVersion.WindowEnvironment.Single().Key);
        foreach (Version? unsafeVersion in (Version?[])[new Version(2, 54, 0), null])
        {
            WebKitDebugging.Plan refused = Decide(options with { WebKitInspector = true }, version: unsafeVersion);
            True(refused.StartAdapter, $"the inspector:// server and the adapter on {unsafeVersion}");
            Equal("WEBKIT_INSPECTOR_SERVER", refused.WindowEnvironment.Single().Key);
            Contains(refused.Hints.Single(), "bug 330602");
        }

        Contains(WebKitDebugging.CdpHint(9222, defaults), "http://127.0.0.1:9222/");
        Contains(WebKitDebugging.CdpHint(9333, defaults), "Port 9222 was taken");
    }

    [Test, DisplayName("the session file lists the CDP adapter and gives an IDE-started Window the inspector address")]
    public void DevSessionListsTheWebKitEndpoints()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration desktop = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["desktop"] };
        var options = new DevOptions(null, "Debug", true, true, true, false, [])
        {
            StartWindow = false,
            DebugEnvironment = [new("WEBKIT_INSPECTOR_SERVER", "127.0.0.1:40000")],
        };
        var session = new DevSession(desktop, options,
            [WebKitDebugging.CdpEndpoint(9222), new("webkitgtk", "webkit-remote-inspector", "inspector://127.0.0.1:40000")],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        using JsonDocument document = JsonDocument.Parse(session.Render());
        JsonElement cdp = document.RootElement.GetProperty("debugEndpoints")[0];
        Equal(("webkitgtk", "cdp", "http://127.0.0.1:9222/"),
            (cdp.GetProperty("engine").GetString(), cdp.GetProperty("protocol").GetString(), cdp.GetProperty("url").GetString()));
        Equal("127.0.0.1:40000", document.RootElement.GetProperty("window").GetProperty("environment").GetProperty("WEBKIT_INSPECTOR_SERVER").GetString());
        IReadOnlyDictionary<string, string?> environment = HostProcessController.CreateDevelopmentEnvironment(desktop,
            new Dictionary<string, string?> { ["RUNIC_APPLICATION_VITE_SERVER"] = "http://127.0.0.1:5173/" }, static _ => null,
            debugEnvironment: options.DebugEnvironment);
        Equal("127.0.0.1:40000", environment["WEBKIT_INSPECTOR_SERVER"]);
    }

    [Test, DisplayName("a debugger attaches through the endpoint to a page of a WebKit inspector server")]
    public async Task CdpEndpointRoundTripsThroughAnInspectorServer()
    {
        using var server = new FakeInspectorServer();
        await using var endpoint = new WebKitDebugEndpoint("127.0.0.1", server.Port);
        True(endpoint.Start(0), "the endpoint listens");
        int port = endpoint.Port!.Value;
        using var http = new HttpClient();

        JsonArray pages = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"))!.AsArray();
        Equal("Fake page", pages.Single()!["title"]!.GetValue<string>());
        string browser = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version"))!["webSocketDebuggerUrl"]!.GetValue<string>();
        StartsWith(browser, $"ws://127.0.0.1:{port}/devtools/browser/");

        using (var foreign = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/json/version"))
        {
            foreign.Headers.Add("Origin", "https://example.com");
            Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);
        }
        using (var wrongSecret = new ClientWebSocket())
        {
            await ThrowsAsync<WebSocketException>(() => wrongSecret.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/devtools/browser/0000"), CancellationToken.None));
        }

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(browser), CancellationToken.None);
        async Task<JsonObject> Command(int id, string method, JsonObject? parameters = null, string? sessionId = null)
        {
            var command = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] };
            if (sessionId is not null) command["sessionId"] = sessionId;
            await socket.SendAsync(Encoding.UTF8.GetBytes(command.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[1 << 16];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, timeout.Token);
                JsonObject message = JsonNode.Parse(buffer.AsSpan(0, received.Count))!.AsObject();
                if (message["id"]?.GetValue<int>() == id) return message;
            }
        }
        string target = pages.Single()!["id"]!.GetValue<string>();
        string session = (await Command(1, "Target.attachToTarget", new JsonObject { ["targetId"] = target, ["flatten"] = true }))["result"]!["sessionId"]!.GetValue<string>();
        JsonObject evaluated = await Command(2, "Runtime.evaluate", new JsonObject { ["expression"] = "6 * 7" }, session);
        Equal(42, evaluated["result"]!["result"]!["value"]!.GetValue<int>());
        lock (server.Commands) Equal("Runtime.evaluate", server.Commands.Last());
    }

    // WebKit's remote inspector server, as far as one page and Runtime.evaluate go.
    private sealed class FakeInspectorServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        internal FakeInspectorServer()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal List<string> Commands { get; } = [];

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ClientAsync(client));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private static byte[] ThreeParts(ulong connection, ulong target, string text)
        {
            byte[] message = Encoding.UTF8.GetBytes(text);
            var parameters = new byte[16 + message.Length + 1];
            BinaryPrimitives.WriteUInt64LittleEndian(parameters, connection);
            BinaryPrimitives.WriteUInt64LittleEndian(parameters.AsSpan(8), target);
            message.CopyTo(parameters, 16);
            return parameters;
        }

        private async Task ClientAsync(TcpClient client)
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                var header = new byte[InspectorWire.HeaderSize];
                try
                {
                    while (true)
                    {
                        await stream.ReadExactlyAsync(header);
                        var body = new byte[InspectorWire.ReadBodySize(header)];
                        await stream.ReadExactlyAsync(body);
                        (string name, byte[] parameters) = InspectorWire.ReadBody(body);
                        switch (name)
                        {
                            case "SetupInspectorClient":
                                await stream.WriteAsync(InspectorWire.Frame("DidSetupInspectorClient",
                                    Encoding.UTF8.GetBytes("InspectorBackend.registerCommand(\"Runtime.evaluate\", null, [], []);\0")));
                                await stream.WriteAsync(InspectorWire.Frame("SetTargetList",
                                    InspectorWire.WriteTargetList(1, [new InspectorTarget(1, 1, "WebPage", "Fake page", "http://127.0.0.1:1/index.html")])));
                                break;
                            case "Setup":
                                await stream.WriteAsync(InspectorWire.Frame("SendMessageToFrontend", ThreeParts(1, 1,
                                    """{"method":"Target.targetCreated","params":{"targetInfo":{"targetId":"page-1","type":"page"}}}""")));
                                break;
                            case "SendMessageToBackend":
                                (ulong connection, ulong target, string outer) = InspectorWire.ReadMessageToFrontend(parameters);
                                JsonObject inner = JsonNode.Parse(JsonNode.Parse(outer)!["params"]!["message"]!.GetValue<string>())!.AsObject();
                                lock (Commands) Commands.Add(inner["method"]!.GetValue<string>());
                                JsonObject result = inner["method"]!.GetValue<string>() == "Runtime.evaluate"
                                    ? new JsonObject { ["result"] = new JsonObject { ["type"] = "number", ["value"] = 42 } }
                                    : [];
                                string dispatched = new JsonObject
                                {
                                    ["method"] = "Target.dispatchMessageFromTarget",
                                    ["params"] = new JsonObject
                                    {
                                        ["targetId"] = "page-1",
                                        ["message"] = new JsonObject { ["id"] = inner["id"]!.GetValue<long>(), ["result"] = result }.ToJsonString(),
                                    },
                                }.ToJsonString();
                                await stream.WriteAsync(InspectorWire.Frame("SendMessageToFrontend", ThreeParts(connection, target, dispatched)));
                                break;
                        }
                    }
                }
                catch (Exception error) when (error is IOException or EndOfStreamException or ObjectDisposedException or InvalidOperationException)
                {
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _listener.Dispose();
        }
    }

    private static void StartsWith(string value, string prefix) => True(value.StartsWith(prefix, StringComparison.Ordinal), $"'{value}' starts with '{prefix}'");

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
