# OS-service conformance and native fixtures

Status: shipping extraction and preview integration in progress, 2026-09-08.
This non-packable test executable consumes the implementation extracted into
`Runic.Platform`, `Runic.Platform.Runtime`, the three OS provider packages,
`Runic.Application.Platform` and `Runic.Application.Platform.Desktop`. The retained
prototype project name identifies the test harness, not a private parallel copy of
the contracts or runtime. See the [OS integration RFC](../../../docs/guides/application/architecture/os-integration-rfc.md)
and [current preview policy](../../../eng/preview-human-acceptance.md).

Current source implementation is not a claim of registry publication or native
certification. Demo-preview manual checks cover this Linux system and the available
Windows VM; automated native JIT/NativeAOT CI remains required on all three OS targets.
Real macOS/sandbox checks, unavailable Wayland, broader accessibility and independent
pilots remain explicit follow-ups before v1. Historical receipts below retain their
original scope and do not certify the extracted candidate.

Run in the repository's Nix development environment:

```sh
dotnet run --project tests/dotnet/Runic.Platform.Prototype.Tests -c Release -p:TreatWarningsAsErrors=true
# Separate process: CS-WebUI owns one process-wide native runtime.
# Prefix with xvfb-run -a on Linux without an active display, even with OpenWindow=false.
dotnet run --project tests/dotnet/Runic.Platform.Prototype.Tests -c Release -- --live-cswebui
# Interactive native session; on Linux X11 CI, prefix with xvfb-run -a.
dotnet run --project tests/dotnet/Runic.Platform.Prototype.Tests -c Release -- --native
```

The CI workflow runs these commands and NativeAOT variants as separate native-job
steps on Linux x64, Windows x64 and macOS Apple Silicon. Use `bun run ci` to run
supported workflow jobs locally. Entering Nix supplies libraries, not a Wayland
compositor, desktop portal or sandbox permission grant.

## Implemented behavior

- Read leases retain an acquired stream and optional access grant. They are
  consumed once, accept unknown-length streams, close outstanding streams on
  disposal and reject reuse. Applications must still bound reads and validate data.
- Save selection does not create/truncate the destination. A transaction stages
  in the same directory, compares the destination's content hash with selection
  time, then attempts one rename. Cancellation before submission prevents commit;
  cancellation after submission waits for the actual result. Unknown outcomes
  cannot be retried. Disposal removes leftover staging files and joins commits.
- Atomic replacement is explicit provider policy. A selected-file portal or
  security-scoped grant does not imply permission to create sibling files: those
  saves return `AtomicReplaceUnavailable` before staging. There is no copy/delete
  fallback. Content hashing is a best-effort conflict check, not filesystem CAS;
  neither concurrent replacement between check and rename nor power-loss durability
  is promised. Network/virtual filesystems require separate provider validation.
- The presentation facade tracks delivered leases. Caller disposal releases early;
  owner shutdown joins the same release and closes forgotten streams. Late cancelled
  selections are released before their request settles. Release failures remain
  observable at shutdown.
- `ApplicationBridgeSessionFactory` resolves presentation lifetime hooks in the
  same async scope as generated features. Reconnection retains the scope. Both
  transports stop these services before waiting for commands that may await them.
  Concurrent shutdown joins one operation and a failing service does not skip
  remaining scope/native teardown.
- Desktop drains scoped services before native close. Explicit embedded hosts with
  lifetime hooks intercept user close, first apply the application's existing close
  policy, then drain services before destroying the owner. Installed-browser and
  fallback presentations do not acquire native ownership by process ID.
- `ApplicationHost.Run()` runs Desktop's macOS event loop from a synchronous process
  entry point, including startup and native cleanup. `RunAsync()` remains useful
  when a suitable owner event loop already exists; it cannot create AppKit's process
  main thread. Native dispatch runs inline on the owner thread, cancels queued work
  and waits for already-running callbacks.

## Native providers

| Provider | Ownership and access handling |
| --- | --- |
| Windows | Common Item Dialog on the WebView2 STA; actual HWND passed to `Show`; cancellation posts `IFileDialog.Close` to that thread; all COM interfaces released after modal completion. File acquisition obeys actual filesystem permissions. |
| Linux | `GtkFileChooserNative` with the actual GtkWindow as transient parent. GTK handles X11/Wayland parent export and portal protocol. Required portal availability is probed off the UI thread. Hide/destroy drains cancellation. Portal-persistent grants are not revoked as if they were SDK-owned. |
| macOS | `NSOpenPanel`/`NSSavePanel` sheets on the process main thread, with an Objective-C completion block owning its captured context. The selected NSURL is retained; successful SDK calls to `startAccessingSecurityScopedResource` are balanced with `stopAccessingSecurityScopedResource`. Sandbox entitlement checks conservatively constrain staging. No persisted bookmark is claimed. |

The shipping integration binds these providers only to a verified Desktop embedded owner.
CS-WebUI has live service-scope parity, but its current public API does not supply
a verified browser HWND/NSWindow/GTK dispatcher. Required owned pickers therefore
remain unavailable there. An installed browser PID is not a substitute. No Desktop
or ASP.NET dependency was added to the CS-WebUI shipping package.

Native API references:
[Common Item Dialog](https://learn.microsoft.com/en-us/windows/win32/shell/common-file-dialog),
[GtkFileChooserNative](https://docs.gtk.org/gtk3/class.FileChooserNative.html),
[AppKit sheets](https://developer.apple.com/documentation/appkit/nssavepanel/beginsheetmodal(for:completionhandler:)),
[macOS sandbox file access](https://developer.apple.com/documentation/security/accessing-files-from-the-macos-app-sandbox).

## Evidence and limits

Historical prototype coverage included 12 grouped scenarios covering admission, late cancellation,
lease/stream cleanup, staged writes, conflicts, uncertain commits, access-acquisition
failures, dispatch and DI composition. Its live Desktop test starts the real host
and drives the session owned by that host through reconnect and blocked-operation
shutdown. The separate CS-WebUI process does the same with its native HTTP runtime.
Additional transport tests exercise a command holding the CS-WebUI mailbox gate.

The `--native` test uses the real Application runner and embedded owner. It opens
and cancels an actual open picker, opens an actual save picker, requests owner
close while that picker is active, and verifies owner-thread access release and
closure of an outstanding acquired stream. It has phase logs and a 90-second
watchdog; unsupported environments fail rather than silently count as passes.
The filesystem selection used to check acquired-stream teardown is injected and
is explicitly **not** sandbox-grant evidence.

Historical Linux GTK/X11 native cancellation and both live host lifetimes were exercised
locally in managed and NativeAOT builds, with warnings treated as errors. Windows/macOS native execution is assigned to their CI runners. Actual
Wayland/portal selection, focus behavior, user permission grants and a signed
macOS sandbox fixture still require the manual evidence listed in
[acceptance scenarios](../../../eng/os-integration-acceptance.md). CI dialog
cancellation must not be presented as that evidence. CI prepares a signed fixture;
use its [manual selection instructions](../../../tests/native/platform-sandbox/README.md)
and `--native-select` to exercise a real user-selected file grant.

Clipboard source implementations now live in the OS provider packages. Windows uses
Win32 clipboard APIs; macOS uses ApplicationServices C Pasteboard APIs and
CoreFoundation, not `NSPasteboard`. Linux uses GTK `UTF8_STRING` selection transfers:
managed decoding/string allocation is bounded, but GTK receives the native transfer
before reporting its size, so that limit does not bound native transfer allocation.
An advertised Linux transfer failure reports `IoError` because GTK does not expose a
separate permission-refusal code. Successful writes acquire selection ownership;
clipboard persistence after process exit is not guaranteed.

Customer/document migration integration, package acceptance and provider footprint
measurements must be judged from their current candidate receipts. Source presence
and historical prototype passes cannot substitute for those results.
