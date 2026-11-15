# Evidence (2026-11-15, Linux x64, Xvfb)

Setup: runic-sdk dev shell (WebKitGTK 2.54.0 for both ABIs, Node 24.22.1, .NET SDK 10.0.403); `dotnet new runic-app`
from the published Runic.Application.Templates 0.19.0 (React, `--linux-gtk 4`; GTK 3 by replacing `WithGtk4()` with
`Linux.EmbeddedBackend = Gtk3WebKit41`); `dotnet runic dev --no-dotnet-watch -e WEBKIT_INSPECTOR_SERVER=127.0.0.1:9340`
(dotnet-runic 0.19.0); vscode-js-debug 1.140.1 standalone DAP server (released 2026-11-10).

## Baseline: js-debug against Chromium 154 (`--remote-debugging-port=9222`), Vite page `spike.html`

```
breakpoint verified: src/spike.ts line 8 col 17
stopped: reason breakpoint, frame button#b.onClick src/spike.ts 8:17
scope Local: onClick  vars event=PointerEvent {…}, target=<button id="b">, this=<button id="b">
console.log seen: true; after next: 9:10; after stepIn: 10:3; continue; PASS
```

CDP js-debug sent on attach + scenario (Chromium): Target.attachToBrowserTarget, Target.setDiscoverTargets,
Target.attachToTarget {flatten}, Target.setAutoAttach {waitForDebuggerOnStart, flatten}, Network.setCacheDisabled,
Browser.getVersion, Page.enable, Page.getResourceTree, ServiceWorker.enable, ServiceWorker.setForceUpdateOnPageLoad,
Network.disable, Page.waitForDebugger, Runtime.enable, Profiler.enable, Debugger.enable, Debugger.setAsyncCallStackDepth,
Debugger.setInstrumentationBreakpoint {beforeScriptWithSourceMapExecution}, Debugger.setBreakpointByUrl (urlRegex, then
url), Runtime.evaluate, Runtime.runIfWaitingForDebugger, Debugger.removeBreakpoint, Runtime.callFunctionOn,
Runtime.getProperties (incl. accessorPropertiesOnly), DOM.describeNode, Debugger.evaluateOnCallFrame, Debugger.stepOver,
Debugger.stepInto {breakOnAsyncCall}, Debugger.resume. Events used: Target.targetCreated/attachedToTarget,
Runtime.executionContextCreated, Debugger.scriptParsed, Debugger.paused/resumed, Runtime.consoleAPICalled.

## First end-to-end run through the adapter (GTK 4 window, `dap-harness.mjs`, App.tsx:31)

```
breakpoint verified: src/App.tsx line 31 col 49
stopped: reason breakpoint; frame onClick src/App.tsx 31:49; executeDispatch node_modules/react-dom/…development.js 20804:9
scope Local: onClick (this=undefined); scope Closure: client={snapshot: <accessor>, subscribe: ƒ, …}, navigate={run: ƒ, pending: false, …}
evaluate at frame: client !== undefined -> true; console.log seen: true; next -> 31:80; continue; PASS
```

## Scenario runs (`dap-scenarios.mjs`, final adapter of this commit)

| Window | Runs | Result |
| --- | --- | --- |
| GTK 4 / WebKitGTK 6.0 | 3 consecutive, one adapter (after the replay-order and enable fixes) | 9/9 ×3 |
| GTK 4 / WebKitGTK 6.0 | 2, fresh adapter each (page with reload + HMR history), final build | 9/9 ×2 |
| GTK 3 / WebKitGTK 4.1 | 5, fresh adapter each, final build | 9/9 ×4; one run: js-debug never connected (no traffic reached the adapter; harness) |

Typical passing run:

```
PASS step-in-out: at onClick src/App.tsx:31:49; stepIn -> Object.run node_modules/@runic-artifex/views/dist/controller.js:214:13; stepOut -> …controller.js:240:9
PASS conditional: false condition: no stop; true condition: stopped
PASS logpoint: output "spike-logpoint client=true", no stop
PASS exception: stopped=exception
PASS uncaught-output: output[stderr] "Uncaught Error Error: spike-uncaught /     at <anonymous> (repl:1:35)"
PASS console-levels: spike-warn:stderr, spike-error:stderr, spike-info:stdout, spike-obj:stdout
PASS set-variable: stopped at CounterPage src/pages/CounterPage.tsx:16:3; locals client=… | counter=… | increment=… | label={value: 'Clicks', …} | state={count: 0, step: 1, label: 'Clicks', …}; hover counter.state?.count -> 0
PASS reload: first render after reload: stopped at App src/App.tsx:21:3; click after reload: stopped at onClick src/App.tsx:31:49
PASS hmr: after HMR edit: stopped at onClick src/App.tsx:31:49; header 'Window/View starter · React (edited)'
9/9 passed
```

Bugs found and fixed during the spike (each reproduced, then passing):

1. WebKit replays existing scripts on `Debugger.enable` in hash order; js-debug binds a source to the last-parsed script →
   attach after HMR bound a stale version. Fix: buffer the replay, emit by script ID.
2. js-debug calls `Debugger.enable` again after each navigation; WebKit answers "Debugger domain already enabled" →
   the replay buffer never closed, every later script was held back. Fix: idempotent `*.enable`, buffer closed in `finally`.
3. The replay also lists scripts of earlier page loads and superseded HMR versions → a breakpoint showed at the
   generated position. Fix: replay only the newest script per URL and per inline source map's `sources`.
4. No `beforeScriptWithSourceMapExecution` in WebKit → a breakpoint in code that runs during load was missed. Fix:
   `Debugger.pause` after a source-mapped script is parsed, reported as an `instrumentation` pause.
5. Uncaught exceptions printed no text (js-debug titles them from `exceptionDetails.exception`). Fix: synthesize it.

## Security (adapter on 127.0.0.1:9229)

```
no Origin, right secret: 101   foreign Origin: 403   foreign Host: 403   wrong secret: 404   Origin on /json/version: 403
```

## Not reproduced

One GTK 3 Window exited without a crash record during an early run (socket reset at 16:45:57, `dotnet runic dev`
exited 0, nothing in the journal or coredumpctl). Not seen in the later 5 GTK 3 and 5 GTK 4 sessions.
