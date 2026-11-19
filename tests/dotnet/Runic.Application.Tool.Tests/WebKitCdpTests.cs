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
        // WebKit's hash of its protocol description: SHA-1 in upper-case hex, up to the byte string's NUL.
        Equal("A9993E364706816ABA3E25717850C26C9CD0D89D", InspectorWire.BackendCommandsHash("abc\0"u8));
        Equal("A9993E364706816ABA3E25717850C26C9CD0D89D\0",
            Encoding.ASCII.GetString(InspectorWire.ReadBody(InspectorWire.SetupInspectorClient("A9993E364706816ABA3E25717850C26C9CD0D89D").AsSpan(InspectorWire.HeaderSize)).Parameters));

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

    [Test, DisplayName("a breakpoint that WebKit sets in a script parsed later reaches the debugger, as with Chromium")]
    public async Task ResolvedBreakpointsReachTheDebugger()
    {
        // A breakpoint set while a reload replaced the script: WebKit has no location yet, and binds it once the new
        // script is parsed. js-debug shows the breakpoint unverified until it hears of that location.
        const string id = "http://h/src/App.tsx:68:13";
        var webKit = new FakeWebKit
        {
            Answer = static (method, _) => method == "Debugger.setBreakpointByUrl" ? Json($$"""{"breakpointId":"{{id}}","locations":[]}""") : [],
        };
        CdpPageSession session = webKit.Session();
        JsonObject set = await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"url":"http://h/src/App.tsx","lineNumber":68,"columnNumber":13}"""));
        Equal(0, set["locations"]!.AsArray().Count);
        const string parsed = """{"scriptId":"4673","url":"http://h/src/App.tsx","startLine":0,"startColumn":0,"endLine":142,"endColumn":0,"sourceMapURL":"data:x"}""";
        const string resolved = $$$"""{"breakpointId":"{{{id}}}","location":{"scriptId":"4673","lineNumber":70,"columnNumber":19}}""";
        await session.TranslateEventAsync("Debugger.scriptParsed", Json(parsed));
        string scriptId = webKit.Emitted[^1].Parameters["scriptId"]!.GetValue<string>();
        await session.TranslateEventAsync("Debugger.breakpointResolved", Json(resolved));
        (string method, JsonObject parameters) = webKit.Emitted[^1];
        Equal(("Debugger.breakpointResolved", id, scriptId, 70, 19), (method, parameters["breakpointId"]!.GetValue<string>(),
            parameters["location"]!["scriptId"]!.GetValue<string>(), parameters["location"]!["lineNumber"]!.GetValue<int>(),
            parameters["location"]!["columnNumber"]!.GetValue<int>()));

        // Not one that the debugger did not set.
        int emitted = webKit.Emitted.Count;
        await session.TranslateEventAsync("Debugger.breakpointResolved", Json("""{"breakpointId":"other","location":{"scriptId":"4673","lineNumber":1,"columnNumber":0}}"""));
        Equal(emitted, webKit.Emitted.Count);

        // In the replay of Debugger.enable, after the replayed scripts.
        await session.TranslateEventAsync(CdpPageSession.ReplayStart, []);
        await session.TranslateEventAsync("Debugger.scriptParsed", Json(parsed));
        await session.TranslateEventAsync("Debugger.breakpointResolved", Json(resolved));
        Equal(emitted, webKit.Emitted.Count);
        await session.TranslateEventAsync(CdpPageSession.ReplayEnd, []);
        SequenceEqual(["Debugger.scriptParsed", "Debugger.breakpointResolved"], [.. webKit.Emitted.Skip(emitted).Select(static e => e.Method)]);
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
            // Two modules whose maps name their source alike: each source resolves against its script's URL (#1108).
            Script("40", "http://h/@vite/client", Map("client")),
            Script("41", "http://h/@id/virtual:runic/client", Map("client")),
        ]);
        SequenceEqual(["3", "4", "5", "7", "31", "40", "41"], [.. current.Select(static script => script["scriptId"]!.GetValue<string>())]);
    }

    [Test, DisplayName("the scripts of a document that WebKit already replaced do not reach the debugger (#1108)")]
    public async Task ScriptsOfAReplacedDocumentAreNotForwarded()
    {
        // The adapter translates WebKit's events after it received them. During reloads in quick succession it still
        // holds the old document's scripts when WebKit has navigated; js-debug would remove such a script while it
        // creates the sources of its source map, and keep an original source bound to the removed script.
        const string id = "http://h/src/App.tsx:68:13";
        var webKit = new FakeWebKit
        {
            Answer = static (method, _) => method == "Debugger.setBreakpointByUrl" ? Json($$"""{"breakpointId":"{{id}}","locations":[]}""") : [],
        };
        CdpPageSession session = webKit.Session();
        await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"url":"http://h/src/App.tsx","lineNumber":68,"columnNumber":13}"""));
        int start = webKit.Emitted.Count;
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"5","url":"http://h/src/App.tsx","endLine":142,"sourceMapURL":"data:x"}"""));
        session.Post("Debugger.breakpointResolved", Json($$$"""{"breakpointId":"{{{id}}}","location":{"scriptId":"5","lineNumber":70,"columnNumber":19}}"""));
        session.Post("Console.messageAdded", Json("""
            {"message":{"source":"console-api","level":"debug","type":"log","text":"[vite] connecting...","parameters":[{"type":"string","value":"x"}],
              "stackTrace":{"callFrames":[{"functionName":"","scriptId":"5","url":"http://h/src/App.tsx","lineNumber":3,"columnNumber":7}]}}}
            """));
        session.Post("Page.frameNavigated", Json("""{"frame":{"id":"0.1","loaderId":"0.2","url":"http://h/index.html"}}"""));
        session.Post("Debugger.globalObjectCleared", []);
        // The new document's script, after the navigation, reaches it.
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"6","url":"http://h/src/App.tsx","endLine":142,"sourceMapURL":"data:x"}"""));
        session.Post("Debugger.breakpointResolved", Json($$$"""{"breakpointId":"{{{id}}}","location":{"scriptId":"6","lineNumber":70,"columnNumber":19}}"""));
        Task events = session.RunAsync(CancellationToken.None);
        await Until(() => webKit.Emitted.Skip(start).Any(static e => e.Method == "Debugger.breakpointResolved"));
        List<(string Method, JsonObject Parameters)> emitted = [.. webKit.Emitted.Skip(start)];
        SequenceEqual(["Runtime.consoleAPICalled", "Runtime.executionContextsCleared", "Page.frameNavigated", "Runtime.executionContextsCleared",
            "Debugger.scriptParsed", "Debugger.breakpointResolved"], [.. emitted.Select(static e => e.Method)]);
        Equal("6", emitted[4].Parameters["scriptId"]!.GetValue<string>());
        Equal("6", emitted[5].Parameters["location"]!["scriptId"]!.GetValue<string>());
        // The old document's console output keeps its place in its file, without the script js-debug would wait for.
        JsonObject frame = emitted[0].Parameters["stackTrace"]!["callFrames"]![0]!.AsObject();
        Equal(("", "http://h/src/App.tsx", 2), (frame["scriptId"]!.GetValue<string>(), frame["url"]!.GetValue<string>(), frame["lineNumber"]!.GetValue<int>()));
        session.Complete();
        await events;
    }

    [Test, DisplayName("the source-map pause is emulated: one per script, before it runs, while WebKit stays stopped (#1051)")]
    public async Task InstrumentationPauseIsEmulated()
    {
        var webKit = new FakeWebKit
        {
            Answer = static (method, _) => method == "Debugger.setBreakpointByUrl" ? Json("""{"breakpointId":"/^/:0:0","locations":[]}""") : [],
        };
        CdpPageSession session = webKit.Session();
        JsonObject instrumentation = await session.HandleAsync("Debugger.setInstrumentationBreakpoint", Json("""{"instrumentation":"beforeScriptWithSourceMapExecution"}"""));
        Equal("instrumentation:beforeScriptWithSourceMapExecution", instrumentation["breakpointId"]!.GetValue<string>());
        // A URL breakpoint at the start of every script: WebKit sets it while it parses a script, before the script runs.
        Equal("""{"urlRegex":"^","lineNumber":0,"columnNumber":0}""", webKit.Sent.Single(static sent => sent.Method == "Debugger.setBreakpointByUrl").Parameters.ToJsonString());
        Task events = session.RunAsync(CancellationToken.None);
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"1","url":"http://h/a.js","sourceMapURL":"a.js.map"}"""));
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"2","url":"http://h/plain.js"}"""));
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"3","url":"http://h/b.js","sourceMapURL":"b.js.map"}"""));
        session.Post("Debugger.paused", Json("""{"reason":"Breakpoint","data":{"breakpointId":"/^/:0:0"},"callFrames":[]}"""));

        // One instrumentation pause per script with a source map, oldest first, each after the debugger answered the last.
        await Until(() => Emitted(webKit, "Debugger.paused") == 1);
        Equal("1", LastPaused(webKit)["data"]!["scriptId"]!.GetValue<string>());
        Equal("instrumentation", LastPaused(webKit)["reason"]!.GetValue<string>());
        // The next one comes only after the reply to js-debug's Debugger.resume, as with Chromium.
        await session.HandleAsync("Debugger.resume", []);
        await Task.Delay(50);
        Equal(1, Emitted(webKit, "Debugger.paused"));
        // Not after the reply to another command that went out first (#1108).
        session.Replied("Runtime.evaluate");
        await Task.Delay(50);
        Equal(1, Emitted(webKit, "Debugger.paused"));
        session.Replied("Debugger.resume");
        await Until(() => Emitted(webKit, "Debugger.paused") == 2);
        Equal("3", LastPaused(webKit)["data"]!["scriptId"]!.GetValue<string>());
        Equal("b.js.map", LastPaused(webKit)["data"]!["sourceMapURL"]!.GetValue<string>());
        Equal(1, Emitted(webKit, "Debugger.resumed"));
        // WebKit stays stopped until the last one is answered, then continues; the debugger saw no other stop.
        False(webKit.Sent.Any(static sent => sent.Method == "Debugger.resume"), "WebKit resumed before the last answer");
        await ResumeAsync(session);
        await Until(() => webKit.Sent.Any(static sent => sent.Method == "Debugger.resume"));
        Equal(2, Emitted(webKit, "Debugger.resumed"));
        session.Post("Debugger.resumed", []);

        // A stop at the instrumentation breakpoint with nothing parsed since is no stop for the debugger.
        session.Post("Debugger.paused", Json("""{"reason":"Breakpoint","data":{"breakpointId":"/^/:0:0"},"callFrames":[]}"""));
        session.Post("Debugger.resumed", []);
        await Until(() => webKit.Sent.Count(static sent => sent.Method == "Debugger.resume") == 2);

        // Another stop reports the queued scripts first, then itself.
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"4","url":"http://h/c.js","sourceMapURL":"c.js.map"}"""));
        session.Post("Debugger.paused", Json("""{"reason":"Breakpoint","data":{"breakpointId":"http://h/c.js:3:0"},"callFrames":[]}"""));
        await Until(() => Emitted(webKit, "Debugger.paused") == 3);
        Equal("4", LastPaused(webKit)["data"]!["scriptId"]!.GetValue<string>());
        await ResumeAsync(session);
        await Until(() => Emitted(webKit, "Debugger.paused") == 4);
        SequenceEqual(["http://h/c.js:3:0"], [.. LastPaused(webKit)["hitBreakpoints"]!.AsArray().Select(static id => id!.GetValue<string>())]);
        Equal(3, Emitted(webKit, "Debugger.resumed"));
        // This answer is WebKit's.
        await ResumeAsync(session);
        Equal(3, webKit.Sent.Count(static sent => sent.Method == "Debugger.resume"));
        session.Post("Debugger.resumed", []);
        await Until(() => Emitted(webKit, "Debugger.resumed") == 4);

        // Removing the instrumentation breakpoint ends the emulation, in WebKit too.
        await session.HandleAsync("Debugger.removeBreakpoint", Json("""{"breakpointId":"instrumentation:beforeScriptWithSourceMapExecution"}"""));
        Equal("/^/:0:0", webKit.Sent[^1].Parameters["breakpointId"]!.GetValue<string>());
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"5","url":"http://h/d.js","sourceMapURL":"d.js.map"}"""));
        session.Post("Debugger.paused", Json("""{"reason":"other","callFrames":[]}"""));
        await Until(() => Emitted(webKit, "Debugger.paused") == 5);
        Equal("other", LastPaused(webKit)["reason"]!.GetValue<string>());
        session.Complete();
        await events;
    }

    [Test, DisplayName("an emulated instrumentation pause ends with the session")]
    public async Task InstrumentationPauseEndsWithTheSession()
    {
        var webKit = new FakeWebKit
        {
            Answer = static (method, _) => method == "Debugger.setBreakpointByUrl" ? Json("""{"breakpointId":"/^/:0:0","locations":[]}""") : [],
        };
        CdpPageSession session = webKit.Session();
        await session.HandleAsync("Debugger.setInstrumentationBreakpoint", Json("""{"instrumentation":"beforeScriptWithSourceMapExecution"}"""));
        Task events = session.RunAsync(CancellationToken.None);
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"1","url":"http://h/a.js","sourceMapURL":"a.js.map"}"""));
        session.Post("Debugger.paused", Json("""{"reason":"Breakpoint","data":{"breakpointId":"/^/:0:0"},"callFrames":[]}"""));
        await Until(() => Emitted(webKit, "Debugger.paused") == 1);
        session.Complete();
        await events.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test, DisplayName("a stop at the instrumentation breakpoint that WebKit reports after the debugger removed it continues (#1139)")]
    public async Task StopAtRemovedInstrumentationBreakpointContinues()
    {
        var webKit = new FakeWebKit
        {
            Answer = static (method, _) => method == "Debugger.setBreakpointByUrl" ? Json("""{"breakpointId":"/^/:0:0","locations":[]}""") : [],
        };
        CdpPageSession session = webKit.Session();
        await session.HandleAsync("Debugger.setInstrumentationBreakpoint", Json("""{"instrumentation":"beforeScriptWithSourceMapExecution"}"""));
        Task events = session.RunAsync(CancellationToken.None);
        // A script without a source map, as a late __devtools/embedded.js. WebKit stops at the instrumentation breakpoint
        // where it starts, but reports the stop only after the debugger removed that breakpoint.
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"9","url":"http://h/__devtools/embedded.js"}"""));
        await session.HandleAsync("Debugger.removeBreakpoint", Json("""{"breakpointId":"instrumentation:beforeScriptWithSourceMapExecution"}"""));
        session.Post("Debugger.paused", Json("""{"reason":"Breakpoint","data":{"breakpointId":"/^/:0:0"},"callFrames":[{"callFrameId":"f","location":{"scriptId":"9","lineNumber":0,"columnNumber":0}}]}"""));
        // No stop for the debugger, which never set that breakpoint: WebKit continues.
        await Until(() => webKit.Sent.Any(static sent => sent.Method == "Debugger.resume"));
        session.Post("Debugger.resumed", []);
        await Task.Delay(100);
        Equal(0, Emitted(webKit, "Debugger.paused"));
        Equal(0, Emitted(webKit, "Debugger.resumed"));

        // Set again, the instrumentation breakpoint stops for its pauses as before.
        await session.HandleAsync("Debugger.setInstrumentationBreakpoint", Json("""{"instrumentation":"beforeScriptWithSourceMapExecution"}"""));
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"10","url":"http://h/a.js","sourceMapURL":"a.js.map"}"""));
        session.Post("Debugger.paused", Json("""{"reason":"Breakpoint","data":{"breakpointId":"/^/:0:0"},"callFrames":[{"callFrameId":"f","location":{"scriptId":"10","lineNumber":0,"columnNumber":0}}]}"""));
        await Until(() => Emitted(webKit, "Debugger.paused") == 1);
        Equal(("instrumentation", "10"), (LastPaused(webKit)["reason"]!.GetValue<string>(), LastPaused(webKit)["data"]!["scriptId"]!.GetValue<string>()));
        session.Complete();
        await events;
    }

    [Test, DisplayName("the debugger's breakpoints at the start of a script still hit where the instrumentation breakpoint stops")]
    public async Task BreakpointsAtAnInstrumentationStopHit()
    {
        var webKit = new FakeWebKit
        {
            Answer = static (method, p) => method switch
            {
                "Debugger.setBreakpointByUrl" when p["urlRegex"]?.GetValue<string>() == "^" => Json("""{"breakpointId":"/^/:0:0","locations":[]}"""),
                "Debugger.setBreakpointByUrl" => Json($$"""{"breakpointId":"{{p["url"]!.GetValue<string>()}}:2:0","locations":[]}"""),
                // A logpoint's condition logs and is false; a breakpoint's condition holds.
                "Debugger.evaluateOnCallFrame" => new JsonObject
                {
                    ["result"] = new JsonObject { ["type"] = "boolean", ["value"] = !p["expression"]!.GetValue<string>().Contains("log", StringComparison.Ordinal) },
                },
                _ => [],
            },
        };
        CdpPageSession session = webKit.Session();
        await session.HandleAsync("Debugger.setInstrumentationBreakpoint", Json("""{"instrumentation":"beforeScriptWithSourceMapExecution"}"""));
        await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"url":"http://h/log.js","lineNumber":2,"condition":"console.log('x'), false"}"""));
        await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"url":"http://h/stop.js","lineNumber":2,"condition":"value > 1"}"""));
        Task events = session.RunAsync(CancellationToken.None);
        const string Frames = """[{"callFrameId":"frame","location":{"scriptId":"SCRIPT","lineNumber":2,"columnNumber":0}}]""";
        // WebKit sets both breakpoints where the scripts start, as the instrumentation breakpoint, and reports that one.
        session.Post("Debugger.breakpointResolved", Json("""{"breakpointId":"http://h/log.js:2:0","location":{"scriptId":"1","lineNumber":2,"columnNumber":0}}"""));
        session.Post("Debugger.breakpointResolved", Json("""{"breakpointId":"http://h/stop.js:2:0","location":{"scriptId":"2","lineNumber":2,"columnNumber":0}}"""));
        session.Post("Debugger.paused", Json($$"""{"reason":"Breakpoint","data":{"breakpointId":"/^/:0:0"},"callFrames":{{Frames.Replace("SCRIPT", "1", StringComparison.Ordinal)}}}"""));
        session.Post("Debugger.resumed", []);
        // The logpoint's condition ran in the frame; it does not stop.
        await Until(() => webKit.Sent.Any(static sent => sent.Method == "Debugger.resume"));
        Equal("!!(\nconsole.log('x'), false\n)", webKit.Sent.Single(static sent => sent.Method == "Debugger.evaluateOnCallFrame").Parameters["expression"]!.GetValue<string>());
        Equal(0, Emitted(webKit, "Debugger.paused"));
        // The breakpoint whose condition holds stops, as WebKit would have stopped for it.
        session.Post("Debugger.paused", Json($$"""{"reason":"Breakpoint","data":{"breakpointId":"/^/:0:0"},"callFrames":{{Frames.Replace("SCRIPT", "2", StringComparison.Ordinal)}}}"""));
        await Until(() => Emitted(webKit, "Debugger.paused") == 1);
        SequenceEqual(["http://h/stop.js:2:0"], [.. LastPaused(webKit)["hitBreakpoints"]!.AsArray().Select(static id => id!.GetValue<string>())]);
        Equal(1, webKit.Sent.Count(static sent => sent.Method == "Debugger.resume"));
        session.Complete();
        await events;
    }

    [Test, DisplayName("a script's own instrumentation pause comes where it starts, after the others, and js-debug's line 0 breakpoint stands for it")]
    public async Task InstrumentationPauseOfTheStartingScriptComesLast()
    {
        var webKit = new FakeWebKit
        {
            Answer = static (method, p) => method switch
            {
                "Debugger.setBreakpointByUrl" when p["urlRegex"]?.GetValue<string>() == "^" => Json("""{"breakpointId":"/^/:0:0","locations":[]}"""),
                "Debugger.setBreakpointByUrl" => Json("""{"breakpointId":"/entry/:0:0","locations":[]}"""),
                _ => [],
            },
        };
        CdpPageSession session = webKit.Session();
        await session.HandleAsync("Debugger.setInstrumentationBreakpoint", Json("""{"instrumentation":"beforeScriptWithSourceMapExecution"}"""));
        // js-debug's own breakpoint at the start of the scripts it has breakpoints for.
        await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"urlRegex":"entry","lineNumber":0,"columnNumber":0}"""));
        Task events = session.RunAsync(CancellationToken.None);
        const string Frames = """[{"callFrameId":"frame","location":{"scriptId":"SCRIPT","lineNumber":1,"columnNumber":0}}]""";
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"1","url":"http://h/a.js","sourceMapURL":"a.js.map"}"""));
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"2","url":"http://h/b.js","sourceMapURL":"b.js.map"}"""));
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"3","url":"http://h/c.js","sourceMapURL":"c.js.map"}"""));
        // b.js starts first: a.js and c.js have their pauses, then b.js.
        session.Post("Debugger.paused", Json($$"""{"reason":"Breakpoint","data":{"breakpointId":"/^/:0:0"},"callFrames":{{Frames.Replace("SCRIPT", "2", StringComparison.Ordinal)}}}"""));
        foreach (string expected in (string[])["1", "3", "2"])
        {
            await Until(() => Emitted(webKit, "Debugger.paused") == Array.IndexOf((string[])["1", "3", "2"], expected) + 1);
            Equal(expected, LastPaused(webKit)["data"]!["scriptId"]!.GetValue<string>());
            await ResumeAsync(session);
        }
        await Until(() => webKit.Sent.Count(static sent => sent.Method == "Debugger.resume") == 1);
        session.Post("Debugger.resumed", []);
        // a.js starts at js-debug's line 0 breakpoint: its own pause again, as WebKit stops there before it runs.
        session.Post("Debugger.breakpointResolved", Json("""{"breakpointId":"/entry/:0:0","location":{"scriptId":"1","lineNumber":1,"columnNumber":0}}"""));
        session.Post("Debugger.paused", Json($$"""{"reason":"Breakpoint","data":{"breakpointId":"/entry/:0:0"},"callFrames":{{Frames.Replace("SCRIPT", "1", StringComparison.Ordinal)}}}"""));
        await Until(() => Emitted(webKit, "Debugger.paused") == 4);
        Equal(("instrumentation", "1"), (LastPaused(webKit)["reason"]!.GetValue<string>(), LastPaused(webKit)["data"]!["scriptId"]!.GetValue<string>()));
        await ResumeAsync(session);
        await Until(() => webKit.Sent.Count(static sent => sent.Method == "Debugger.resume") == 2);
        Equal(4, Emitted(webKit, "Debugger.paused"));
        session.Post("Debugger.resumed", []);
        // js-debug removes that breakpoint once it has the source map, but WebKit can stop at it before it reads that:
        // c.js's own pause again, and no stop at a breakpoint the debugger no longer has.
        await session.HandleAsync("Debugger.removeBreakpoint", Json("""{"breakpointId":"/entry/:0:0"}"""));
        session.Post("Debugger.paused", Json($$"""{"reason":"Breakpoint","data":{"breakpointId":"/entry/:0:0"},"callFrames":{{Frames.Replace("SCRIPT", "3", StringComparison.Ordinal)}}}"""));
        await Until(() => Emitted(webKit, "Debugger.paused") == 5);
        Equal(("instrumentation", "3"), (LastPaused(webKit)["reason"]!.GetValue<string>(), LastPaused(webKit)["data"]!["scriptId"]!.GetValue<string>()));
        await ResumeAsync(session);
        await Until(() => webKit.Sent.Count(static sent => sent.Method == "Debugger.resume") == 3);
        Equal(5, Emitted(webKit, "Debugger.paused"));
        session.Complete();
        await events;
    }

    [Test, DisplayName("a breakpoint where a script with a source map starts is js-debug's to report at the script's instrumentation pause")]
    public async Task BreakpointWhereAMappedScriptStartsIsReportedOnce()
    {
        var webKit = new FakeWebKit
        {
            Answer = static (method, p) => method == "Debugger.setBreakpointByUrl"
                ? Json($$"""{"breakpointId":"{{p["url"]?.GetValue<string>() ?? "/^/"}}:0:0","locations":[]}""")
                : [],
        };
        CdpPageSession session = webKit.Session();
        await session.HandleAsync("Debugger.setInstrumentationBreakpoint", Json("""{"instrumentation":"beforeScriptWithSourceMapExecution"}"""));
        // The breakpoint on the script's first statement, from before a reload: WebKit sets it, not the instrumentation one.
        await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"url":"http://h/a.js","lineNumber":0,"columnNumber":0}"""));
        Task events = session.RunAsync(CancellationToken.None);
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"1","url":"http://h/a.js","sourceMapURL":"a.js.map"}"""));
        session.Post("Debugger.breakpointResolved", Json("""{"breakpointId":"http://h/a.js:0:0","location":{"scriptId":"1","lineNumber":0,"columnNumber":0}}"""));
        session.Post("Debugger.paused", Json("""{"reason":"Breakpoint","data":{"breakpointId":"http://h/a.js:0:0"},"callFrames":[{"callFrameId":"frame","location":{"scriptId":"1","lineNumber":0,"columnNumber":0}}]}"""));
        await Until(() => Emitted(webKit, "Debugger.paused") == 1);
        Equal(("instrumentation", "1"), (LastPaused(webKit)["reason"]!.GetValue<string>(), LastPaused(webKit)["data"]!["scriptId"]!.GetValue<string>()));
        await ResumeAsync(session);
        await Until(() => webKit.Sent.Any(static sent => sent.Method == "Debugger.resume"));
        Equal(1, Emitted(webKit, "Debugger.paused"));
        session.Complete();
        await events;
    }

    [Test, DisplayName("the reply to a resume, step, pause or navigation reaches the debugger before the events WebKit sends after it (#1108)")]
    public async Task RepliesComeBeforeTheEventsTheyCause()
    {
        var webKit = new FakeWebKit();
        CdpPageSession session = webKit.Session();
        Task events = session.RunAsync(CancellationToken.None);
        const string Paused = """{"reason":"other","callFrames":[]}""";

        // WebKit answers Debugger.resume, then reports that it resumed and the next pause. js-debug gets them only after
        // the reply: one that came before it would be lost, and the page would stay stopped (#1108).
        foreach (string command in (string[])["Debugger.resume", "Debugger.stepOver", "Debugger.stepInto", "Debugger.stepOut"])
        {
            int resumed = Emitted(webKit, "Debugger.resumed"), paused = Emitted(webKit, "Debugger.paused");
            await session.HandleAsync(command, []);
            Equal(command, webKit.Sent[^1].Method);
            // An event of another kind is not held.
            session.Post("Debugger.scriptParsed", Json($$"""{"scriptId":"{{command}}","url":"http://h/{{command}}.js"}"""));
            await Until(() => Parsed(webKit, command));
            session.Post("Debugger.resumed", []);
            session.Post("Debugger.paused", Json(Paused));
            await Task.Delay(100);
            Equal((resumed, paused), (Emitted(webKit, "Debugger.resumed"), Emitted(webKit, "Debugger.paused")));
            session.Replied(command);
            await Until(() => Emitted(webKit, "Debugger.paused") == paused + 1);
            Equal(resumed + 1, Emitted(webKit, "Debugger.resumed"));
            lock (webKit.Emitted) Equal("Debugger.resumed", webKit.Emitted[^2].Method);
        }

        // Debugger.pause: the pause it asked for comes after its reply.
        await session.HandleAsync("Debugger.pause", []);
        session.Post("Debugger.paused", Json(Paused));
        await Task.Delay(100);
        Equal(4, Emitted(webKit, "Debugger.paused"));
        session.Replied("Debugger.pause");
        await Until(() => Emitted(webKit, "Debugger.paused") == 5);

        // A navigation's reply comes before every event of the new document.
        await session.HandleAsync("Page.reload", []);
        session.Post("Debugger.scriptParsed", Json("""{"scriptId":"after-reload","url":"http://h/after-reload.js"}"""));
        await Task.Delay(100);
        False(Parsed(webKit, "after-reload"), "an event passed the reload's reply");
        session.Replied("Page.reload");
        await Until(() => Parsed(webKit, "after-reload"));

        // A command that gets no reply (the debugger left) holds nothing; neither does a session that ends.
        await session.HandleAsync("Debugger.resume", []);
        session.ReplyDropped("Debugger.resume");
        session.Post("Debugger.paused", Json(Paused));
        await Until(() => Emitted(webKit, "Debugger.paused") == 6);
        await session.HandleAsync("Debugger.resume", []);
        session.Post("Debugger.paused", Json(Paused));
        session.Complete();
        await events.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(7, Emitted(webKit, "Debugger.paused"));
    }

    // js-debug's Debugger.resume, and its reply.
    private static async Task ResumeAsync(CdpPageSession session)
    {
        await session.HandleAsync("Debugger.resume", []);
        session.Replied("Debugger.resume");
    }

    // Whether the debugger got the script with this ID.
    private static bool Parsed(FakeWebKit webKit, string scriptId)
    {
        lock (webKit.Emitted) return webKit.Emitted.Any(sent => sent.Method == "Debugger.scriptParsed" && sent.Parameters["scriptId"]?.GetValue<string>() == scriptId);
    }

    private static int Emitted(FakeWebKit webKit, string method)
    {
        lock (webKit.Emitted) return webKit.Emitted.Count(sent => sent.Method == method);
    }

    private static JsonObject LastPaused(FakeWebKit webKit)
    {
        lock (webKit.Emitted) return webKit.Emitted.Last(static sent => sent.Method == "Debugger.paused").Parameters;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var waited = TimeSpan.Zero; !condition(); waited += TimeSpan.FromMilliseconds(10))
        {
            if (waited > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("The condition did not hold within 10 s.");
            await Task.Delay(10);
        }
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

    [Test, DisplayName("a session waits for a restarted Window while the old one exits, and ends when another page takes its place (#1210)")]
    public void RestartWaitDependsOnTheInspectorConnection()
    {
        static InspectorTarget Page(long generation, ulong id) => new(1, id, "WebPage", "Page", "http://127.0.0.1:1/index.html") { Generation = generation };
        var main = Page(3, 1);
        var settings = Page(3, 2);
        IReadOnlySet<string> peers = new HashSet<string>([settings.PageId], StringComparer.Ordinal);
        // The connection closed, or a newer one (the restarted Window) is open: the Window process exited; wait.
        False(CdpClient.ClosedInRunningWindow(null, [], 3, peers), "closed connection");
        False(CdpClient.ClosedInRunningWindow(4, [Page(4, 1)], 3, peers), "restarted Window");
        // Still open, and it lists only what it listed when the page went away, or nothing: an exiting Window process can
        // drop its pages one at a time, however slowly; wait.
        False(CdpClient.ClosedInRunningWindow(3, [settings], 3, peers), "the other page of an exiting Window");
        False(CdpClient.ClosedInRunningWindow(3, [], 3, peers), "no page yet");
        // A page that was not there: the Window process runs on with another page in this one's place; end.
        True(CdpClient.ClosedInRunningWindow(3, [settings, Page(3, 7)], 3, peers), "a new page in a running Window");
    }

    [Test, DisplayName("on a restarted Window's page, the debugger's commands wait until its breakpoints are set again (#1108)")]
    public async Task CommandsWaitForTheRestartedWindowsBreakpoints()
    {
        var webKit = new FakeWebKit
        {
            Answer = static (method, parameters) => method == "Debugger.setBreakpointByUrl"
                ? new JsonObject { ["breakpointId"] = CdpPageSession.UrlBreakpointId(parameters), ["locations"] = new JsonArray() }
                : [],
        };
        CdpPageSession session = webKit.Session();
        await session.HandleAsync("Debugger.enable", []);
        await session.HandleAsync("Debugger.setBreakpointByUrl", Json("""{"url":"http://h/App.tsx","lineNumber":68,"columnNumber":13}"""));
        session.Suspend();

        // The restarted page answers the breakpoint late, as a loaded WebKit does.
        var sent = new List<string>();
        var breakpoint = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonObject> Restarted(string method, JsonObject? parameters, CancellationToken cancellationToken)
        {
            lock (sent) sent.Add(method);
            return method == "Debugger.setBreakpointByUrl" ? breakpoint.Task : Task.FromResult(new JsonObject());
        }
        Task resumed = session.ResumeAsync(Restarted, WebKitProtocol.Unknown);
        // The scenario's click: it reaches the page only once the breakpoint is set there.
        Task<JsonObject> click = session.HandleAsync("Runtime.evaluate", Json("""{"expression":"click()"}"""));
        await Task.Delay(100);
        False(click.IsCompleted, "the click was answered before the breakpoint was set");
        lock (sent) False(sent.Contains("Runtime.evaluate"), "the click reached the page before the breakpoint");
        breakpoint.SetResult(Json("""{"breakpointId":"http://h/App.tsx:68:13","locations":[]}"""));
        await resumed;
        await click;
        lock (sent) True(sent.IndexOf("Debugger.setBreakpointByUrl") < sent.IndexOf("Runtime.evaluate"), "the breakpoint came first");

        // A Window that goes away again while the session is set up holds no command.
        session.Suspend();
        var never = new TaskCompletionSource<JsonObject>();
        _ = session.ResumeAsync((_, _, _) => never.Task, WebKitProtocol.Unknown);
        session.Suspend();
        Equal(CdpException.ServerError, (await ThrowsAsync<CdpException>(() => session.HandleAsync("Runtime.evaluate", Json("""{"expression":"1"}""")))).Code);
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
        WebKitDebugging.Plan exposed = Decide(options, name => name == "WEBKIT_INSPECTOR_SERVER" ? "0.0.0.0:5555" : null);
        False(exposed.StartAdapter, "a non-loopback address");
        Contains(exposed.Hints[^1], "[dev] Warning: WEBKIT_INSPECTOR_SERVER=0.0.0.0:5555 is not a loopback address");
        // WEBKIT_INSPECTOR_HTTP_SERVER replaces the socket server in WebKit, so no adapter; it is checked like --webkit-inspector.
        WebKitDebugging.Plan http = Decide(options, name => name == "WEBKIT_INSPECTOR_HTTP_SERVER" ? "127.0.0.1:7777" : null,
            version: new Version(2, 56, 0));
        Equal((false, "webkit-inspector"), (http.StartAdapter, http.Endpoints.Single().Protocol));
        Contains(http.Hints.Single(), "ignores WEBKIT_INSPECTOR_SERVER");
        foreach (Version? unsafeVersion in (Version?[])[new Version(2, 54, 2), new Version(2, 55, 4), null])
        {
            WebKitDebugging.Plan unsafeHttp = Decide(options with { Environment = [new("WEBKIT_INSPECTOR_HTTP_SERVER", "127.0.0.1:7777")] },
                version: unsafeVersion);
            Equal(2, unsafeHttp.Hints.Count);
            Contains(unsafeHttp.Hints[1], "[dev] Warning: WebKitGTK");
            Contains(unsafeHttp.Hints[1], "any web page");
        }
        WebKitDebugging.Plan exposedHttp = Decide(options, name => name == "WEBKIT_INSPECTOR_HTTP_SERVER" ? "0.0.0.0:7777" : null,
            version: new Version(2, 56, 0));
        Contains(exposedHttp.Hints[^1], "WEBKIT_INSPECTOR_HTTP_SERVER=0.0.0.0:7777 is not a loopback address");

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
        // Under --no-window the IDE's Window may load another WebKitGTK than dev.
        Contains(Decide(options with { WebKitInspector = true, StartWindow = false }, version: new Version(2, 54, 0)).Hints.Single(),
            "The check used the WebKitGTK that dev loads");

        Contains(WebKitDebugging.CdpHints(9222, defaults).Single(), "http://127.0.0.1:9222/");
        Contains(WebKitDebugging.CdpHints(9333, defaults).Single(), "Port 9222 was taken");
        // Another user holds 9222: an attach configuration that keeps it would reach their process.
        IReadOnlyList<string> squatted = WebKitDebugging.CdpHints(9333, defaults, new(1001, FixedAttach: ["Attach to the page (JavaScript)"]));
        Equal(2, squatted.Count);
        Contains(squatted[1], "[dev] Warning: port 9222 belongs to another user (uid 1001)");
        Contains(WebKitDebugging.ForeignInspectorHint("127.0.0.1", 9223, 1001), "127.0.0.1:9223 belongs to another user (uid 1001)");
    }

    [Test, DisplayName("only WebKitGTK releases with the fix for bug 330602 count as fixed")]
    public void HttpInspectorVersionGate()
    {
        foreach ((Version? version, bool expected) in (ValueTuple<Version?, bool>[])[
            (null, false), (new Version(2, 52, 9), false), (new Version(2, 54, 2), false), (new Version(2, 54, 3), true),
            (new Version(2, 54, 10), true), (new Version(2, 55, 0), false), (new Version(2, 55, 9), false),
            (new Version(2, 56, 0), true), (new Version(2, 57, 1), true), (new Version(3, 0, 0), true)])
        {
            Equal(expected, WebKitDebugging.HttpInspectorIsFixed(version));
        }
    }

    // As /proc/net/tcp and /proc/net/tcp6 print them on a little-endian machine: 0100007F is 127.0.0.1, 2406 is 9222.
    private const string SyntheticTcp = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 0100007F:2406 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 101 1 0000000000000000 100 0 0 10 0
           1: 0100007F:2406 0100007F:A000 01 00000000:00000000 00:00000000 00000000  1000        0 102 1 0000000000000000 20 4 30 10 -1
           2: 0100007F:A000 0100007F:2406 01 00000000:00000000 00:00000000 00000000  1001        0 103 1 0000000000000000 20 4 30 10 -1
           3: 0100007F:A001 0100007F:2406 06 00000000:00000000 03:00001770 00000000     0        0 0 3 0000000000000000
           4: 00000000:240F 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1002        0 104 1 0000000000000000 100 0 0 10 0
           5: 0100007F:2406 0100007F:A003 01 00000000:00000000 00:00000000 00000000  1000        0 105 1 0000000000000000 20 4 30 10 -1
           6: 0100007F:A003 0100007F:2406 01 00000000:00000000 00:00000000 00000000  1000        0 106 1 0000000000000000 20 4 30 10 -1
           7: not a row
        """;

    private const string SyntheticTcp6 = """
          sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 0000000000000000FFFF00000100007F:A002 0000000000000000FFFF00000100007F:2406 01 00000000:00000000 00:00000000 00000000  1003        0 107 1 0000000000000000 20 4 30 10 -1
           1: 00000000000000000000000001000000:2410 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1004        0 108 1 0000000000000000 100 0 0 10 0
        """;

    [Test, DisplayName("the owner of a loopback connection's other end and of a listener, from /proc/net/tcp")]
    public void LoopbackSocketOwnersFromProcNetTcp()
    {
        var sockets = new LoopbackSockets(path => path.EndsWith("tcp6", StringComparison.Ordinal) ? SyntheticTcp6 : SyntheticTcp, 1000);
        var adapter = new IPEndPoint(IPAddress.Loopback, 9222);
        IPEndPoint Client(int port) => new(IPAddress.Loopback, port);
        Equal(1001u, sockets.PeerOwner(adapter, Client(0xA000)));
        Equal(1000u, sockets.PeerOwner(adapter, Client(0xA003)));
        Equal(1003u, sockets.PeerOwner(adapter, Client(0xA002)));
        Equal((uint?)null, sockets.PeerOwner(adapter, Client(0xA001)));
        Equal((uint?)null, sockets.PeerOwner(adapter, Client(0xBEEF)));

        SequenceEqual([1000u], sockets.ListenerOwners(IPAddress.Loopback, 9222));
        Equal((uint?)null, sockets.ForeignListener(IPAddress.Loopback, 9222));
        Equal(1000u, new LoopbackSockets(path => path.EndsWith("tcp6", StringComparison.Ordinal) ? SyntheticTcp6 : SyntheticTcp, 1001)
            .ForeignListener(IPAddress.Loopback, 9222));
        // A wildcard listener receives loopback connections too; ::1 is not 127.0.0.1.
        SequenceEqual([1002u], sockets.ListenerOwners(IPAddress.Loopback, 0x240F));
        SequenceEqual([1004u], sockets.ListenerOwners(IPAddress.IPv6Loopback, 0x2410));
        SequenceEqual(Array.Empty<uint>(), sockets.ListenerOwners(IPAddress.Loopback, 0x2410));
        SequenceEqual(Array.Empty<uint>(), new LoopbackSockets(static _ => null, 1000).ListenerOwners(IPAddress.Loopback, 9222));
        Equal(7, LoopbackSockets.Parse(SyntheticTcp).Count);
    }

    // The machine's real tables with the owner of the rows that match replaced, as another user's sockets would look.
    private static LoopbackSockets ForeignOwner(Func<LoopbackSockets.Entry, bool> foreign) => new(path =>
    {
        string table = File.ReadAllText(path);
        return string.Join('\n', table.Split('\n').Select(line =>
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (LoopbackSockets.Parse("x\n" + line).SingleOrDefault() is not { Local: not null } entry || !foreign(entry)) return line;
            fields[7] = "4242";
            return string.Join(' ', fields);
        }));
    }, LoopbackSockets.Current!.User);

    [Test, DisplayName("the CDP adapter closes connections of other local users and reports them once")]
    public async Task CdpEndpointRefusesOtherUsers()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var server = new FakeInspectorServer();
        var reported = new List<string>();
        await using var endpoint = new WebKitDebugEndpoint("127.0.0.1", server.Port,
            ForeignOwner(static entry => entry.State == 1 && entry.Remote.Port != 0), line => { lock (reported) reported.Add(line); });
        True(endpoint.Start(0), "the endpoint listens");
        using var http = new HttpClient();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await ThrowsAsync<HttpRequestException>(() => http.GetStringAsync($"http://127.0.0.1:{endpoint.Port}/json/version"));
        }
        lock (reported) Contains(reported.Single(), "from another user (uid 4242)");
    }

    [Test, DisplayName("the CDP adapter does not connect to an inspector port that another local user listens on")]
    public async Task CdpEndpointRefusesAnotherUsersInspector()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var server = new FakeInspectorServer();
        var reported = new List<string>();
        await using var endpoint = new WebKitDebugEndpoint("127.0.0.1", server.Port,
            ForeignOwner(entry => entry.State == LoopbackSockets.ListenState && entry.Local.Port == server.Port), line => { lock (reported) reported.Add(line); });
        True(endpoint.Start(0), "the endpoint listens");
        using var http = new HttpClient();
        Equal("[]", await http.GetStringAsync($"http://127.0.0.1:{endpoint.Port}/json/list"));
        lock (reported) Contains(reported.Single(), $"127.0.0.1:{server.Port} belongs to another user (uid 4242)");
        lock (server.Commands) Equal(0, server.Commands.Count);
    }

    [Test, DisplayName("RUNIC_DEV_CDP_TRACE's file is readable by its user only")]
    public void CdpTraceFileIsPrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        string path = Path.Combine(Path.GetTempPath(), $"runic-cdp-trace-{Guid.NewGuid():N}.log");
        try
        {
            CdpTrace.Append(path, "one\n");
            CdpTrace.Append(path, "two\n");
            Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Equal("one\ntwo\n", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test, DisplayName("RUNIC_DEV_CDP_TRACE keeps the start of long strings, such as script sources, only")]
    public void CdpTraceShortensLongStrings()
    {
        string source = string.Concat(Enumerable.Repeat("const a = \\\"x\\\";\\n", 1000));
        string response = $$$"""{"id":7,"result":{"scriptSource":"{{{source}}}","base64Encoded":false}}""";
        string shortened = CdpTrace.ShortenLongStrings(response);
        True(shortened.Length < 400, $"shortened to {shortened.Length} characters");
        StartsWith(shortened, "{\"id\":7,\"result\":{\"scriptSource\":\"const a = \\\"x\\\";");
        Contains(shortened, "characters left out; RUNIC_DEV_CDP_TRACE_SOURCES=1 keeps them]\",\"base64Encoded\":false}}");
        // The cut ends after a whole escape sequence: the kept start is still a valid JSON string.
        string kept = shortened[(shortened.IndexOf(":\"", StringComparison.Ordinal) + 2)..shortened.IndexOf('…', StringComparison.Ordinal)];
        JsonSerializer.Deserialize<string>($"\"{kept}\"");
        string small = """{"id":8,"result":{"result":{"type":"number","value":42}}}""";
        Equal(small, CdpTrace.ShortenLongStrings(small));
        // "data:application/json;base64," and 170 characters put a \u escape across the 200th character.
        string unicode = $$"""{"sourceMapURL":"data:application/json;base64,{{new string('A', 170)}}\u00e9{{new string('B', 5000)}}"}""";
        JsonSerializer.Deserialize<string>($"\"{CdpTrace.ShortenLongStrings(unicode)[17..].Split('…')[0]}\"");
    }

    [Test, DisplayName("under --no-window, a taken port 9222 is an error that names the holder")]
    public void NoWindowCdpPortTakenMessage()
    {
        Equal("RAPPDEV1017", WebKitDebugging.NoWindowCdpPortTakenCode);
        Contains(WebKitDebugging.NoWindowCdpPortTakenMessage(null), "--no-window needs port 9222");
        Contains(WebKitDebugging.NoWindowCdpPortTakenMessage(null), "another dotnet runic dev session");
        Contains(WebKitDebugging.NoWindowCdpPortTakenMessage(1001), "another user (uid 1001) holds it");
        string named = WebKitDebugging.NoWindowCdpPortTakenMessage(null, "another dotnet runic dev session (process 4242, in /src/Other)");
        Contains(named, "but another dotnet runic dev session (process 4242, in /src/Other) holds it. Stop another dotnet runic dev session");
    }

    [Test, DisplayName("a taken port 9222 names its holder and the project's attach configurations that expect it")]
    public void TakenCdpPortNamesTheHolderAndTheAttachConfiguration()
    {
        // The holder: this user's listener on 9222 (inode 777), and the process whose descriptor points to that socket.
        const string table = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:2406 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 777 1 0 100 0 0 10 0
               1: 0100007F:2407 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 778 1 0 100 0 0 10 0
            """;
        var sockets = new LoopbackSockets(path => path.EndsWith("tcp", StringComparison.Ordinal) ? table : null, 1000);
        SequenceEqual([777UL], sockets.OwnListenerInodes(IPAddress.Loopback, 9222));
        using var workspace = new TestWorkspace();
        if (!OperatingSystem.IsWindows())
        {
            string proc = Path.Combine(workspace.Root, "proc");
            foreach ((string pid, string inode, string command, string cwd) in (ValueTuple<string, string, string, string>[])[
                ("100", "999", "bash\0", "/home/me"),
                ("4242", "777", "/usr/bin/dotnet\0/home/me/.nuget/packages/dotnet-runic/0.22.0/tools/net10.0/any/dotnet-runic.dll\0dev\0", "/src/Other")])
            {
                Directory.CreateDirectory(Path.Combine(proc, pid, "fd"));
                File.CreateSymbolicLink(Path.Combine(proc, pid, "fd", "3"), $"socket:[{inode}]");
                File.WriteAllText(Path.Combine(proc, pid, "cmdline"), command);
                Directory.CreateSymbolicLink(Path.Combine(proc, pid, "cwd"), cwd);
            }
            Directory.CreateDirectory(Path.Combine(proc, "self"));
            Equal("another dotnet runic dev session (process 4242, in /src/Other)", PortHolder.Describe(sockets, IPAddress.Loopback, 9222, proc));
            Equal(null, PortHolder.Describe(sockets, IPAddress.Loopback, 9333, proc));
        }
        Equal("process 7 (chromium --remote-debugging-port=9222)", PortHolder.Name(7, ["/usr/lib/chromium/chromium", "--remote-debugging-port=9222"], "/"));
        True(PortHolder.IsDevCommandLine(["dotnet-runic", "dev", "--no-window"]), "the apphost");
        False(PortHolder.IsDevCommandLine(["dotnet-runic", "doctor"]), "another command");

        // The project's launch.json (JSONC): only JavaScript attach configurations on the fixed port count.
        workspace.Write(".git/HEAD", "ref: refs/heads/main");
        workspace.Write(".vscode/launch.json", """
            {
              "version": "0.2.0",
              "configurations": [
                // The page.
                { "name": "Attach to the page (JavaScript)", "type": "msedge", "request": "attach", "port": 9222, "useWebView": true },
                { "name": "Chrome attach", "type": "chrome", "request": "attach", "port": "9222" },
                { "name": "Elsewhere", "type": "msedge", "request": "attach", "port": 9333 },
                { "name": "Window", "type": "coreclr", "request": "launch", "port": 9222 },
              ],
            }
            """);
        string project = Path.Combine(workspace.Root, "src", "App");
        Directory.CreateDirectory(project);
        (string? launch, IReadOnlyList<string> names) = PortHolder.FixedAttachConfigurations(project, 9222);
        Equal(Path.Combine(workspace.Root, ".vscode", "launch.json"), launch);
        SequenceEqual(["Attach to the page (JavaScript)", "Chrome attach"], names);
        Equal(0, PortHolder.FixedAttachConfigurations(Path.Combine(Path.GetTempPath(), "runic-no-such-" + Guid.NewGuid().ToString("N")), 9222).Names.Count);

        // dev's lines: the move names the holder; the attach configuration that expects 9222 is a warning.
        WebKitDebugging.Plan plan = WebKitDebugging.Plan.None;
        var taken = new WebKitDebugging.DefaultPortTaken(null, "another dotnet runic dev session (process 4242, in /src/Other)",
            ".vscode/launch.json", ["Attach to the page (JavaScript)"]);
        IReadOnlyList<string> hints = WebKitDebugging.CdpHints(40123, plan, taken);
        Equal(2, hints.Count);
        Contains(hints[0], "Port 9222 was taken by another dotnet runic dev session (process 4242, in /src/Other).");
        Contains(hints[1], "[dev] Warning: \"Attach to the page (JavaScript)\" in .vscode/launch.json attaches to port 9222, which would " +
            "reach another dotnet runic dev session (process 4242, in /src/Other), not this Window. Set \"port\": 40123 there");
        IReadOnlyList<string> noConfiguration = WebKitDebugging.CdpHints(40123, plan, taken with { FixedAttach = [] });
        Contains(noConfiguration.Single(), "Port 9222 was taken by another dotnet runic dev session (process 4242, in /src/Other): set \"port\": 40123");
        Equal(1, WebKitDebugging.CdpHints(9222, plan, taken).Count);
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
        // This user's own debugger and inspector server pass the checks of LoopbackSockets on Linux.
        var reported = new List<string>();
        await using var endpoint = new WebKitDebugEndpoint("127.0.0.1", server.Port, LoopbackSockets.Current, line => { lock (reported) reported.Add(line); });
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
        lock (reported) Equal(0, reported.Count);
    }

    [Test, DisplayName("another client taking WebKit's inspector detaches the debugger, and the old connection sends nothing more")]
    public async Task ReplacedInspectorConnectionDetachesTheDebugger()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var server = new FakeInspectorServer();
        // The machine's TCP tables, without the other client's connection until it holds the slot.
        // -1: nothing hidden (a listener's row has remote port 0).
        int hidden = -1;
        LoopbackSockets sockets = new(path => string.Join('\n', File.ReadAllText(path).Split('\n').Where(line =>
            LoopbackSockets.Parse("x\n" + line).SingleOrDefault() is not { Local: not null } entry
            || (entry.Local.Port != Volatile.Read(ref hidden) && entry.Remote.Port != Volatile.Read(ref hidden)))), LoopbackSockets.Current!.User);
        var lines = new List<string>();
        var lineWritten = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var endpoint = new WebKitDebugEndpoint("127.0.0.1", server.Port, sockets, line =>
            {
                lock (lines) lines.Add(line);
                lineWritten.TrySetResult(line);
            })
        {
            WatchInterval = TimeSpan.FromMilliseconds(100),
            ProbeTimeout = TestTimeouts.Scale(TimeSpan.FromMilliseconds(500)),
        };
        True(endpoint.Start(0), "the endpoint listens");
        using var ide = await CdpSocket.ConnectAsync(endpoint.Port!.Value);
        string target = (await ide.CommandAsync("Target.getTargets"))["result"]!["targetInfos"]![0]!["targetId"]!.GetValue<string>();
        string session = (await ide.CommandAsync("Target.attachToTarget", new() { ["targetId"] = target, ["flatten"] = true }))["result"]!["sessionId"]!.GetValue<string>();
        Equal(42, (await ide.CommandAsync("Runtime.evaluate", new() { ["expression"] = "6 * 7" }, session))["result"]!["result"]!["value"]!.GetValue<int>());

        // Another client sets up: WebKit gives it the slot and tells the adapter nothing. The adapter sees its connection
        // only once it holds the slot, so every probe goes to the other client.
        using var other = new TcpClient(AddressFamily.InterNetwork);
        other.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int otherPort = ((IPEndPoint)other.Client.LocalEndPoint!).Port;
        Volatile.Write(ref hidden, otherPort);
        await other.ConnectAsync(IPAddress.Loopback, server.Port);
        await other.GetStream().WriteAsync(InspectorWire.SetupInspectorClient());
        await server.WaitForHolderAsync(otherPort);
        Volatile.Write(ref hidden, -1);
        JsonObject detached = await ide.EventAsync("Inspector.detached");
        Equal((session, CdpClient.ReplacedReason), (detached["sessionId"]?.GetValue<string>(), detached["params"]!["reason"]!.GetValue<string>()));
        Equal(session, (await ide.EventAsync("Target.detachedFromTarget"))["params"]!["sessionId"]!.GetValue<string>());
        // dev prints its line after the sessions ended.
        Equal(WebKitDebugEndpoint.ReplacedLine($"127.0.0.1:{server.Port}"), await lineWritten.Task.WaitScaledAsync(TimeSpan.FromSeconds(10)));

        // Until the other client leaves and afterwards, the replaced connection sent only its probe: no Setup that could
        // meet the empty slot, no FrontendDidClose that could end the other client's session.
        other.Close();
        await server.WaitForClientsAsync(0);
        lock (server.ReceivedWithoutSlot) SequenceEqual(["SendMessageToBackend"], server.ReceivedWithoutSlot.Distinct().ToList());
        // The debugger that was attached does not reconnect the endpoint by itself.
        Equal(0, (await ide.CommandAsync("Target.getTargets"))["result"]!["targetInfos"]!.AsArray().Count);
        Equal(2, server.Connections);

        // The IDE attaches again: dev connects anew and the page works; WebKit never got a Setup without a client.
        using var again = await CdpSocket.ConnectAsync(endpoint.Port.Value);
        string next = (await again.CommandAsync("Target.getTargets"))["result"]!["targetInfos"]![0]!["targetId"]!.GetValue<string>();
        True(target != next, "the page has a new target ID on the new connection");
        string resumed = (await again.CommandAsync("Target.attachToTarget", new() { ["targetId"] = next, ["flatten"] = true }))["result"]!["sessionId"]!.GetValue<string>();
        Equal(42, (await again.CommandAsync("Runtime.evaluate", new() { ["expression"] = "6 * 7" }, resumed))["result"]!["result"]!["value"]!.GetValue<int>());
        False(server.SetupWithoutClient, "a Setup reached WebKit's empty client slot");
        lock (lines) Equal(1, lines.Count);
    }

    [Test, DisplayName("a page that does not answer the debugger's first command is reported, tried again, and then fails clearly")]
    public async Task UnansweredFirstCommandIsReported()
    {
        // #1065: right after WebKit's Web Inspector opened, a page did not answer Runtime.enable, and the attach ended
        // after 13 s without a word.
        TimeSpan firstAnswer = TestTimeouts.Scale(TimeSpan.FromMilliseconds(400));
        async Task<(JsonObject Reply, List<string> Lines, List<string> Commands, TimeSpan Elapsed)> AttachAndSend(int unanswered, string method)
        {
            using var server = new FakeInspectorServer();
            var lines = new List<string>();
            await using var endpoint = new WebKitDebugEndpoint("127.0.0.1", server.Port, null, line => { lock (lines) lines.Add(line); })
            {
                FirstAnswerTimeout = firstAnswer,
            };
            True(endpoint.Start(0), "the endpoint listens");
            using var ide = await CdpSocket.ConnectAsync(endpoint.Port!.Value);
            string target = (await ide.CommandAsync("Target.getTargets"))["result"]!["targetInfos"]![0]!["targetId"]!.GetValue<string>();
            string session = (await ide.CommandAsync("Target.attachToTarget", new() { ["targetId"] = target, ["flatten"] = true }))["result"]!["sessionId"]!.GetValue<string>();
            Volatile.Write(ref server.UnansweredPageCommands, unanswered);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            JsonObject reply = await ide.CommandAsync(method, new() { ["expression"] = "6 * 7" }, session);
            elapsed.Stop();
            lock (server.Commands) lock (lines) return (reply, [.. lines], [.. server.Commands], elapsed.Elapsed);
        }

        // A page that never answers: dev says so at once, sends Runtime.enable again, and then the command fails with
        // an error that names it, instead of the debugger's session closing in silence.
        (JsonObject silent, List<string> silentLines, List<string> silentCommands, TimeSpan silentTime) = await AttachAndSend(int.MaxValue, "Runtime.enable");
        Contains(silent["error"]!["message"]!.GetValue<string>(), "did not answer Runtime.enable, also when sent again");
        SequenceEqual([WebKitPage.NoAnswerLine("Runtime.enable", firstAnswer, retrying: true), WebKitPage.StillNoAnswerLine("Runtime.enable")], silentLines);
        Contains(silentLines[1], "Close WebKit's Web Inspector in the Window");
        Equal(2, silentCommands.Count(static command => command == "Runtime.enable"));
        True(silentTime < TestTimeouts.Scale(TimeSpan.FromSeconds(10)), $"the failure took {silentTime}");

        // An inspector that settles: the second try is answered, and the attach goes on after one line.
        (JsonObject settled, List<string> settledLines, List<string> settledCommands, _) = await AttachAndSend(1, "Runtime.enable");
        Equal(0, settled["result"]!.AsObject().Count);
        Equal(1, settledLines.Count);
        Contains(settledLines[0], "dev sends it again");
        Equal(2, settledCommands.Count(static command => command == "Runtime.enable"));

        // A command that may not run twice is not sent again.
        (JsonObject evaluated, List<string> evaluateLines, List<string> evaluateCommands, _) = await AttachAndSend(int.MaxValue, "Runtime.evaluate");
        Contains(evaluated["error"]!["message"]!.GetValue<string>(), "did not answer Runtime.evaluate");
        Equal(1, evaluateCommands.Count(static command => command == "Runtime.evaluate"));
        Contains(evaluateLines.Single(), "Close WebKit's Web Inspector in the Window");
    }

    [Test, DisplayName("a Setup on a connection that lost WebKit's slot unnoticed claims the slot first")]
    public async Task SetupClaimsTheInspectorSlotFirst()
    {
        using var server = new FakeInspectorServer();
        // Without the TCP tables the adapter cannot tell that it was replaced.
        await using var endpoint = new WebKitDebugEndpoint("127.0.0.1", server.Port, null, static _ => { });
        True(endpoint.Start(0), "the endpoint listens");
        using var ide = await CdpSocket.ConnectAsync(endpoint.Port!.Value);
        string target = (await ide.CommandAsync("Target.getTargets"))["result"]!["targetInfos"]![0]!["targetId"]!.GetValue<string>();
        await ide.CommandAsync("Target.attachToTarget", new() { ["targetId"] = target, ["flatten"] = true });

        // Another client takes the slot and leaves: WebKit's slot is empty, and a plain Setup would crash the Window.
        using (var other = new TcpClient())
        {
            await other.ConnectAsync(IPAddress.Loopback, server.Port);
            await other.GetStream().WriteAsync(InspectorWire.SetupInspectorClient());
            await server.WaitForHolderAsync(((IPEndPoint)other.Client.LocalEndPoint!).Port);
        }
        await server.WaitForClientsAsync(1);

        string second = (await ide.CommandAsync("Target.attachToTarget", new() { ["targetId"] = target, ["flatten"] = true }))["result"]!["sessionId"]!.GetValue<string>();
        Equal(42, (await ide.CommandAsync("Runtime.evaluate", new() { ["expression"] = "6 * 7" }, second))["result"]!["result"]!["value"]!.GetValue<int>());
        False(server.SetupWithoutClient, "a Setup reached WebKit's empty client slot");
        // The claim's answer is the empty protocol description: the adapter keeps the one it has.
        True(server.ShortDescriptions > 0, "the claim used WebKit's hash of the protocol description");
    }

    [Test, DisplayName("the inspector server's peers come from Linux's TCP tables, both ends and closed connections included")]
    public void LoopbackSocketsListTheInspectorServersPeers()
    {
        // 127.0.0.1:45349 (B125) listening, the Window's own connection (AF7C), another client in TIME_WAIT (C86E) and a
        // connection elsewhere that uses the same port number (to 10.0.0.1).
        const string table = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:B125 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 36440807 1 0 100 0 0 10 0
               1: 0100007F:B125 0100007F:AF7C 01 00000000:00000000 00:00000000 00000000  1000        0 36446005 1 0 20 4 30 10 -1
               2: 0100007F:AF7C 0100007F:B125 01 00000000:00000000 00:00000000 00000000  1000        0 36446004 1 0 20 4 30 10 -1
               3: 0100007F:C86E 0100007F:B125 06 00000000:00000000 03:00001234 00000000     0        0 0 3 0
               4: 0100007F:D000 0100000A:B125 01 00000000:00000000 00:00000000 00000000  1000        0 36446010 1 0 20 4 30 10 -1
            """;
        SequenceEqual([0xAF7C, 0xC86E], new LoopbackSockets(path => path.EndsWith("tcp", StringComparison.Ordinal) ? table : null, 1000)
            .Peers(IPAddress.Loopback, 0xB125).Order().ToList());
        const string table6 = """
              sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0000000000000000FFFF00000100007F:9C40 0000000000000000FFFF00000100007F:B125 01 00000000:00000000 00:00000000 00000000  1000        0 1 1 0 20 4 30 10 -1
            """;
        SequenceEqual([0x9C40], new LoopbackSockets(path => path.EndsWith("tcp6", StringComparison.Ordinal) ? table6 : null, 1000)
            .Peers(IPAddress.Loopback, 0xB125).ToList());
    }

    // A CDP WebSocket of a debugger at the endpoint's browser target, with its commands' replies and its events.
    private sealed class CdpSocket : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly List<JsonObject> _received = [];
        private readonly SemaphoreSlim _arrived = new(0);
        private int _id;

        internal static async Task<CdpSocket> ConnectAsync(int port)
        {
            using var http = new HttpClient();
            string browser = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version"))!["webSocketDebuggerUrl"]!.GetValue<string>();
            var socket = new CdpSocket();
            await socket._socket.ConnectAsync(new Uri(browser), CancellationToken.None);
            _ = Task.Run(socket.ReceiveAsync);
            return socket;
        }

        private async Task ReceiveAsync()
        {
            var buffer = new byte[1 << 16];
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult received = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    lock (_received) _received.Add(JsonNode.Parse(buffer.AsSpan(0, received.Count))!.AsObject());
                    _arrived.Release();
                }
            }
            catch (Exception error) when (error is WebSocketException or ObjectDisposedException)
            {
            }
        }

        internal async Task<JsonObject> CommandAsync(string method, JsonObject? parameters = null, string? sessionId = null)
        {
            int id = Interlocked.Increment(ref _id);
            var command = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] };
            if (sessionId is not null) command["sessionId"] = sessionId;
            await _socket.SendAsync(Encoding.UTF8.GetBytes(command.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            return await WaitAsync(message => message["id"]?.GetValue<int>() == id, $"the answer to {method}");
        }

        internal Task<JsonObject> EventAsync(string method) => WaitAsync(message => message["method"]?.GetValue<string>() == method, method);

        private async Task<JsonObject> WaitAsync(Func<JsonObject, bool> match, string what)
        {
            using CancellationTokenSource timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(20));
            while (true)
            {
                lock (_received)
                {
                    if (_received.FirstOrDefault(match) is { } found)
                    {
                        _received.Remove(found);
                        return found;
                    }
                }
                try
                {
                    await _arrived.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"No {what} within {TestTimeouts.Scale(TimeSpan.FromSeconds(20)).TotalSeconds} s.");
                }
            }
        }

        public void Dispose()
        {
            _socket.Dispose();
            _arrived.Dispose();
        }
    }

    // WebKit's remote inspector server, as far as one page and Runtime.evaluate go, with its single client slot
    // (RemoteInspectorServer.cpp in WebKitGTK 2.54): the last client that set up gets the target list and every page's
    // messages; when it leaves, the server closes the pages it inspected and the slot is empty, where the real server
    // sends a Setup's page messages to no client and crashes the Window.
    private sealed class FakeInspectorServer : IDisposable
    {
        private const string BackendCommands = "InspectorBackend.registerCommand(\"Runtime.evaluate\", null, [], []);\n" +
            "InspectorBackend.registerCommand(\"Runtime.enable\", null, [], []);";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly HashSet<Client> _clients = [];
        private readonly HashSet<(ulong, ulong)> _inspected = [];
        private Client? _holder;

        internal FakeInspectorServer()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        private sealed class Client(TcpClient connection)
        {
            internal TcpClient Connection { get; } = connection;
            internal SemaphoreSlim Write { get; } = new(1, 1);
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal List<string> Commands { get; } = [];

        /// <summary>The messages from clients that did not hold the slot.</summary>
        internal List<string> ReceivedWithoutSlot { get; } = [];

        /// <summary>A Setup arrived while no client held the slot: the real server crashes the Window.</summary>
        internal bool SetupWithoutClient { get; private set; }

        /// <summary>How many clients connected so far.</summary>
        internal int Connections { get; private set; }

        /// <summary>How many of the next page commands get no answer, as after WebKit's Web Inspector opened (#1065).</summary>
        internal int UnansweredPageCommands;

        /// <summary>How many SetupInspectorClient answers were empty because the client had the protocol already.</summary>
        internal int ShortDescriptions { get; private set; }

        internal async Task WaitForClientsAsync(int count)
        {
            for (var deadline = System.Diagnostics.Stopwatch.StartNew(); deadline.Elapsed < TestTimeouts.Scale(TimeSpan.FromSeconds(5));)
            {
                lock (_gate)
                {
                    if (_clients.Count == count) return;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException($"The fake inspector server did not get to {count} clients.");
        }

        internal async Task WaitForHolderAsync(int clientPort)
        {
            for (var deadline = System.Diagnostics.Stopwatch.StartNew(); deadline.Elapsed < TestTimeouts.Scale(TimeSpan.FromSeconds(5));)
            {
                lock (_gate)
                {
                    if ((_holder?.Connection.Client.RemoteEndPoint as IPEndPoint)?.Port == clientPort) return;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("The other client did not get the slot.");
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var client = new Client(connection);
                    lock (_gate)
                    {
                        _clients.Add(client);
                        Connections++;
                    }
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

        private static async Task SendAsync(Client? client, byte[] frame)
        {
            if (client is null) return;
            await client.Write.WaitAsync();
            try
            {
                await client.Connection.GetStream().WriteAsync(frame);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                client.Write.Release();
            }
        }

        // The page's message for the client that holds the slot, as the real server routes it.
        private Task ToFrontendAsync(ulong connection, ulong target, string text)
        {
            Client? holder;
            lock (_gate) holder = _inspected.Contains((connection, target)) ? _holder : null;
            return SendAsync(holder, InspectorWire.Frame("SendMessageToFrontend", ThreeParts(connection, target, text)));
        }

        private async Task ClientAsync(Client client)
        {
            NetworkStream stream = client.Connection.GetStream();
            var header = new byte[InspectorWire.HeaderSize];
            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(header);
                    var body = new byte[InspectorWire.ReadBodySize(header)];
                    await stream.ReadExactlyAsync(body);
                    (string name, byte[] parameters) = InspectorWire.ReadBody(body);
                    lock (_gate)
                    {
                        if (name != "SetupInspectorClient" && _holder != client) lock (ReceivedWithoutSlot) ReceivedWithoutSlot.Add(name);
                    }
                    switch (name)
                    {
                        case "SetupInspectorClient":
                        {
                            byte[] commands = [.. Encoding.UTF8.GetBytes(BackendCommands), 0];
                            bool known = Encoding.ASCII.GetString(parameters).TrimEnd('\0') == InspectorWire.BackendCommandsHash(commands);
                            lock (_gate)
                            {
                                _holder = client;
                                if (known) ShortDescriptions++;
                            }
                            await SendAsync(client, InspectorWire.Frame("DidSetupInspectorClient", known ? [0] : commands));
                            await SendAsync(client, InspectorWire.Frame("SetTargetList",
                                InspectorWire.WriteTargetList(1, [new InspectorTarget(1, 1, "WebPage", "Fake page", "http://127.0.0.1:1/index.html")])));
                            break;
                        }
                        case "Setup":
                        {
                            (ulong connection, ulong target) = (BinaryPrimitives.ReadUInt64LittleEndian(parameters), BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(8)));
                            lock (_gate)
                            {
                                if (_holder is null) SetupWithoutClient = true;
                                _inspected.Add((connection, target));
                            }
                            await ToFrontendAsync(connection, target, """{"method":"Target.targetCreated","params":{"targetInfo":{"targetId":"page-1","type":"page"}}}""");
                            break;
                        }
                        case "FrontendDidClose":
                            lock (_gate) _inspected.Remove((BinaryPrimitives.ReadUInt64LittleEndian(parameters), BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(8))));
                            break;
                        case "SendMessageToBackend":
                        {
                            (ulong connection, ulong target, string text) = InspectorWire.ReadMessageToFrontend(parameters);
                            JsonObject outer = JsonNode.Parse(text)!.AsObject();
                            if (outer["params"]?["message"]?.GetValue<string>() is not { } innerText)
                            {
                                // A command on the page's own channel, such as the adapter's probe: the UI process answers.
                                await ToFrontendAsync(connection, target, new JsonObject
                                {
                                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "domain was not found" },
                                    ["id"] = outer["id"]!.GetValue<long>(),
                                }.ToJsonString());
                                break;
                            }
                            JsonObject inner = JsonNode.Parse(innerText)!.AsObject();
                            lock (Commands) Commands.Add(inner["method"]!.GetValue<string>());
                            if (Interlocked.Decrement(ref UnansweredPageCommands) >= 0) break;
                            JsonObject result = inner["method"]!.GetValue<string>() == "Runtime.evaluate"
                                ? new JsonObject { ["result"] = new JsonObject { ["type"] = "number", ["value"] = 42 } }
                                : [];
                            await ToFrontendAsync(connection, target, new JsonObject
                            {
                                ["method"] = "Target.dispatchMessageFromTarget",
                                ["params"] = new JsonObject
                                {
                                    ["targetId"] = "page-1",
                                    ["message"] = new JsonObject { ["id"] = inner["id"]!.GetValue<long>(), ["result"] = result }.ToJsonString(),
                                },
                            }.ToJsonString());
                            break;
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or EndOfStreamException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                lock (_gate)
                {
                    _clients.Remove(client);
                    if (_holder == client)
                    {
                        _holder = null;
                        _inspected.Clear();
                    }
                }
                client.Connection.Dispose();
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
