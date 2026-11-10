using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// The WebView2 document-start scripts that report to the host with a token (WindowsMenuKeys, FileDropSupport) run in the
// page's own world, so page script must not see the token: a page that learned it could forge key reports, which run
// the guest's shortcuts, or drops. The scripts run in Node with a minimal WebView2 shim, after which page script hooks
// Object.prototype (toJSON, setters and getters of the token's member) and replaces JSON.stringify and postMessage.
public sealed class InjectedScriptTokenTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    // The shim, the host's script, then the page's hooks; then a trusted key or drop, and for drops a synthetic one.
    private const string Harness = """
        "use strict";
        const fs = require("fs");
        const [scriptPath, kind, token] = process.argv.slice(2);
        const posted = [];
        const listeners = {};
        const timers = [];
        globalThis.top = globalThis;
        globalThis.Element = class Element {};
        globalThis.chrome = { webview: {
          postMessage(message) { posted.push(message); },
          postMessageWithAdditionalObjects(message, files) { posted.push(message); },
        } };
        globalThis.addEventListener = (type, listener) => { (listeners[type] ??= []).push(listener); };
        globalThis.setTimeout = callback => { timers.push(callback); return 0; };
        (0, eval)(fs.readFileSync(scriptPath, "utf8"));

        // The page's script: everything it can hook after the host's script ran.
        const stringify = JSON.stringify;
        const leaked = [];
        let hooked = 0;
        const seen = value => {
          hooked++;
          let text;
          try { text = typeof value === "object" && value !== null ? Object.keys(value).map(key => key + "=" + String(value[key])).join(";") : String(value); }
          catch { text = ""; }
          if (text.includes(token)) leaked.push(text);
        };
        Object.defineProperty(Object.prototype, "toJSON", { configurable: true, writable: true, value: function () { seen(this); return this; } });
        for (const member of ["runicMenuKey", "runicFileDrop", "keyCode", "key", "target", "effect"])
          Object.defineProperty(Object.prototype, member, { configurable: true, get() { seen(this); return undefined; }, set(value) { seen(value); } });
        JSON.stringify = (value, ...rest) => { seen(value); return stringify(value, ...rest); };
        globalThis.chrome.webview.postMessage = message => { seen(message); };
        globalThis.chrome.webview.postMessageWithAdditionalObjects = message => { seen(message); };
        // The hooks work: page script sees an ordinary object that JSON.stringify writes.
        JSON.stringify({ control: true });
        const control = hooked > 0;

        const run = event => {
          for (const listener of listeners[kind === "key" ? "keydown" : "drop"] ?? []) listener(event);
          for (const callback of timers.splice(0)) callback();
        };
        if (kind === "key") {
          run({ isTrusted: true, isComposing: false, key: "Enter", keyCode: 13, ctrlKey: false, shiftKey: false, altKey: false, metaKey: false,
            defaultPrevented: false, getModifierState: () => false });
        } else {
          const drop = trusted => ({ isTrusted: trusted, target: null, preventDefault() {},
            dataTransfer: { types: ["Files"], files: { length: 1 }, dropEffect: "copy" } });
          run(drop(false));
          run(drop(true));
        }
        delete Object.prototype.toJSON;
        for (const member of ["runicMenuKey", "runicFileDrop", "keyCode", "key", "target", "effect"]) delete Object.prototype[member];
        process.stdout.write(stringify({ control, posted, leaked }));
        """;

    [Test]
    public async Task ThePageDoesNotSeeTheKeyReportsToken()
    {
        var result = await RunAsync(WindowsMenuKeys.Script(Token), "key");
        var posted = result["posted"]!.AsArray();
        Assert.Single(posted);
        Assert.Equal(new MenuKeyReport(13, "Enter", false, false, false, false), WindowsMenuKeys.Parse(posted[0]!.GetValue<string>(), Token));
        Assert.Empty(result["leaked"]!.AsArray());
    }

    [Test]
    public async Task ThePageDoesNotSeeTheDropsTokenAndCannotDispatchADrop()
    {
        var result = await RunAsync(FileDropSupport.WebView2Script(Token), "drop");
        // The synthetic drop is not reported; the user's is, with the token.
        var posted = result["posted"]!.AsArray();
        Assert.Single(posted);
        Assert.NotNull(FileDropMessage.Parse(posted[0]!.GetValue<string>(), Token));
        Assert.Empty(result["leaked"]!.AsArray());
    }

    private static async Task<JsonObject> RunAsync(string script, string kind)
    {
        var directory = Directory.CreateTempSubdirectory("runic-injected-script-");
        try
        {
            var scriptPath = Path.Combine(directory.FullName, "script.js");
            var harnessPath = Path.Combine(directory.FullName, "harness.js");
            await File.WriteAllTextAsync(scriptPath, script);
            await File.WriteAllTextAsync(harnessPath, Harness);
            var start = new ProcessStartInfo("node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = directory.FullName,
            };
            foreach (var argument in (string[])[harnessPath, scriptPath, kind, Token]) start.ArgumentList.Add(argument);
            Process process;
            try { process = Process.Start(start) ?? throw new InvalidOperationException("Node did not start."); }
            catch (Win32Exception)
            {
                Skip.Test("Node.js is not installed: the injected scripts cannot run.");
                throw;
            }
            using (process)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, $"Node exited with {process.ExitCode}: {await error}");
                var result = JsonNode.Parse(await output)!.AsObject();
                Assert.True(result["control"]!.GetValue<bool>(), "The page's hooks did not see an ordinary object.");
                return result;
            }
        }
        finally
        {
            try { directory.Delete(recursive: true); }
            catch (IOException) { }
        }
    }
}
