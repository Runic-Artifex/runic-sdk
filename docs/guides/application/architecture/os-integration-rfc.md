# RFC: OS services for Runic applications

Status: proposed API with an [internal lifetime prototype](../../../../tests/dotnet/Runic.Platform.Prototype.Tests/README.md),
2026-09-07. This document does not introduce public packages or claim native
picker/clipboard support. The existing
[host selection contract](../../desktop/host-selection.md) remains authoritative
for shipped behavior. Implementation starts after the current native lifecycle
failures have been diagnosed.

## Outcome

A developer migrates a WPF or MAUI feature by preserving its domain services,
replacing viewmodel orchestration with Runic commands, and injecting explicit OS
services. Choosing Desktop or CS-WebUI does not change those commands, generated
contracts, business rules or frontend components. Desktop enhancements remain
available through its own APIs.

The first feature is customer contact import/export and text copy/paste. It must
exercise native dialogs, cancellation, file-access lifetime, dispatching and
reconnect through the actual bridge. It replaces the reference's HTML file input
when native services are configured. A developer can explicitly retain HTML file
input/download as a separate presentation implementation with different guarantees.

## Ownership and package direction

Provisional package names and locations:

| Package | Responsibility | Dependencies and lifetime |
| --- | --- | --- |
| `Runic.Platform` under `packages/dotnet` | Small C# service contracts, options and result types | No Desktop, CS-WebUI, ASP.NET Core, WPF, MAUI or Toolkit dependency |
| `Runic.Platform.Windows` | Native Windows providers | Platform contracts; COM/Win32 implementation, NativeAOT compatible |
| `Runic.Platform.Linux` | Portal file access and Linux clipboard providers | Platform contracts; explicit desktop-session/backend prerequisites |
| `Runic.Platform.MacOS` | AppKit providers and file-access lifetime | Platform contracts; process-main-thread integration |
| Existing `Runic.Application.Desktop` / `Runic.Application.CsWebUi` | Register selected providers and bind them to owned presentations | Optional integration; do not force native providers into every application |
| Test fixtures under `tests/dotnet` | Controllable providers, dispatchers and ownership fixtures | Internal until a second application demonstrates a reusable testing package |

Application features depend only on contracts for services they use. Domain
libraries remain independent. Native providers share implementation between the
hosts; host integrations supply owner identity, dispatch access and shutdown.
Platform-provider selection is explicit at composition time, with only the target
OS implementation published. Do not load providers by reflection or assembly scans.
Keep the baseline CS-WebUI package free of Desktop and ASP.NET Core references.

A singleton native backend owns OS resources. A presentation-scoped service facade
binds calls to a live owner and cancellation lifetime. A session must not acquire
another window's owner through a frontend-supplied handle. Transport reconnection
can reattach to the same presentation generation; replacing a presentation creates
a new generation and invalidates old owners and pending interactions.

There is no universal `InvokeNative(string, object)` bridge method. Application
commands expose feature intent, such as `ChooseContactImport` or `ExportContact`.
Native handles, access leases, paths and streams stay in C#. Publish only bounded,
typed feature data and intentionally selected display metadata to TypeScript.

## Capability model

Preserve `ApplicationCompositionManifest.Capabilities` as the declaration of what
an application uses. The existing `ApplicationCapabilityProjection` is an immutable
host projection built before startup; it cannot represent a newly created window,
a lost portal connection or a closed owner. Neither concrete application host
currently implements `IApplicationCapabilityProvider`; this is implementation work,
not an existing dynamic service registry.

Add a presentation-scoped `IPlatformCapabilities` service for current availability.
It returns an immutable snapshot with a generation and stable reason codes. Each
operation rechecks its prerequisites; a previous available result is not a grant
of access or a guarantee that the next operation will succeed. The host's manifest
projection reports composition support; the scoped service reports readiness.
Document those meanings separately and project the scoped result into feature
state when a frontend needs it. Do not silently turn the existing immutable
projection into mutable state.

Proposed capability identities:

| Identity | Exact promise when currently available |
| --- | --- |
| `platform.files.open` | Native single-file selection and a disposable read-access lease |
| `platform.files.save` | Native destination selection and a disposable save target |
| `platform.dialogs.owned` | Provider can attach a dialog to this live presentation |
| `platform.files.atomic-replace` | Provider can stage and replace on a supported target; target-specific checks still apply |
| `platform.clipboard.read-text` | Provider can attempt an explicit text read in this session |
| `platform.clipboard.write-text` | Provider can attempt an explicit text write in this session |
| `platform.ui.dispatch` | A live dispatcher for this native UI owner is available |

Use the existing Available/Unavailable distinction with reasons such as
`provider-not-configured`, `desktop-session-unavailable`, `owner-unavailable`,
`owner-closed`, `main-thread-runner-required` and `backend-unavailable`.
Permission denial, busy resources and user dismissal are operation results,
not permanent lack of OS support. Clipboard read and write are separate capabilities.
Do not add a generic `platform.supported` flag.

### Current support and proposed implementation matrix

All picker, clipboard and shared-dispatch entries below are proposals. A target
means an implementation route, not a certified SDK feature.

| Presentation / environment | Native file selection target | Owned-dialog target | Clipboard / dispatcher target | Current gate |
| --- | --- | --- | --- | --- |
| Desktop embedded, Windows | Common Item Dialog | Current HWND and generation | Win32 clipboard; owning UI dispatcher | Implement providers and acceptance |
| Desktop embedded, Linux X11 | XDG FileChooser portal | Export X11 parent identity | GTK clipboard; GTK dispatcher | Test portal-present and portal-missing sessions |
| Desktop embedded, Linux Wayland | XDG FileChooser portal | Export a valid Wayland parent token | GTK clipboard; GTK dispatcher | Prove parent export and compositor behavior |
| Desktop embedded, macOS arm64 | NSOpenPanel / NSSavePanel | Sheet attached to current NSWindow | NSPasteboard; main-thread dispatcher | Fix lifecycle runner evidence and Application main-thread hosting first |
| Desktop installed browser | Shared OS provider if configured | Unavailable until reliable owner integration is proved | OS provider can be independent of browser | Require explicit permission to open an unowned dialog |
| CS-WebUI embedded or installed browser | Same shared OS provider if configured | Unavailable until the integration supplies a verified owner/dispatcher | Same OS provider; host must provide the required native loop | Do not infer ownership from a process ID or from bridge connectivity |
| Headless host / no desktop session | Unavailable | Unavailable | Unavailable unless a specific configured service supports the operation | A listener alone is not a desktop session |
| Android / iOS | Deferred | Deferred | Deferred | No mobile-host support claim; validate a MAUI-derived feature separately |

The existing Desktop close-confirmation API stays separate. CS-WebUI's current
explicit rejection of required close veto is unchanged by this proposal.

Windows dialogs accept an owner HWND and distinguish cancellation from other
failures. The provider maps those outcomes rather than treating every failed
HRESULT as dismissal. [Microsoft IModalWindow.Show](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-imodalwindow-show)

Linux's portal accepts a parent identifier, returns selected file URIs and may
provide document access. X11 and Wayland use different parent identifiers; a raw
Wayland pointer is not the required exported token. Portal filters assist selection
and do not validate file content. These facts motivate separate ownership and
access contracts. [FileChooser](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.FileChooser.html),
[window identifiers](https://flatpak.github.io/xdg-desktop-portal/docs/window-identifiers.html)

macOS sandbox access can involve security-scoped URLs and bookmarks. The provider
must balance access it acquires and distinguish picker-provided access from access
started by the application. The first contract deliberately makes no cross-process
retention promise. [Apple sandbox file access](https://developer.apple.com/documentation/security/accessing-files-from-the-macos-app-sandbox)

## Proposed C# contract shapes

These sketches describe the API for review; names can change before implementation.
All service methods accept caller cancellation. Argument errors throw; normal OS
outcomes use typed results. Calling with an already-cancelled token throws
`OperationCanceledException` before opening UI or acquiring access.

```csharp
public interface IFileDialogs
{
    ValueTask<PlatformResult<IReadFileLease>> OpenFileAsync(
        OpenFileOptions options, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<ISaveFileLease>> SaveFileAsync(
        SaveFileOptions options, CancellationToken cancellationToken = default);
}

public interface IReadFileLease : IAsyncDisposable
{
    string DisplayName { get; }
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

public interface ISaveFileLease : IAsyncDisposable
{
    string DisplayName { get; }
    ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(
        FileWritePolicy policy, CancellationToken cancellationToken = default);
}

public interface IFileWriteTransaction : IAsyncDisposable
{
    Stream Content { get; }
    ValueTask<FileCommitResult> CommitAsync(
        CancellationToken cancellationToken = default);
}

public interface ITextClipboard
{
    ValueTask<PlatformResult<string?>> ReadTextAsync(
        int maximumCharacters, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<Unit>> WriteTextAsync(
        string text, CancellationToken cancellationToken = default);
}

public interface IUiDispatcher
{
    bool CheckAccess();
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
```

`PlatformResult<T>` is a closed result family: `Success(T)`, `Dismissed`,
`Unavailable(reason)`, `Failed(code)`. Do not implement independent nullable value,
error and status properties that permit contradictory states. `Dismissed` is valid
for picker UI only. A clipboard read with no text succeeds with null; empty text
succeeds with an empty string. Caller cancellation throws until an irreversible
operation has begun, as described below. Stable failure codes include
`permission-denied`, `resource-busy`, `invalid-data`, `too-large` and `io-error`.
Unexpected provider bugs remain exceptions. Detailed native errors go to opt-in,
redacted diagnostics; paths and clipboard contents are not logged by default.

The internal prototype refines this sketch into separate `PickerResult<T>` and
`PlatformResult<T>` families. Only picker results can express dismissal, and a
selected result requires a non-null lease. This prevents invalid clipboard
dismissal states without relying on a provider convention. Public API naming
remains subject to the later review gate.

Open options specify title, named extension/MIME filters and owner policy. Save
options additionally specify a suggested filename. Native format mapping belongs
to each provider. The first slice supports one file, text/JSON filters and a required
owner by default. An application may explicitly select `AllowUnowned`; return
`Unavailable(owner-unavailable)` rather than silently ignoring required ownership.
Do not expose an arbitrary start path in a bridge payload. Directory picking,
multiple selection and retained bookmarks are subsequent capability additions.

### File access and save semantics

- A lease owns SDK-acquired access until disposed. Streams must be closed before
  the lease; disposing a lease closes outstanding SDK streams and prevents new
  opens. Disposal is idempotent. All accesses stay tied to their selected resource.
- Lease disposal releases resources the SDK acquired. It does not claim to revoke
  all permissions the OS granted, especially persistent portal document access.
- File size metadata is a hint. The consuming feature enforces its byte limit
  while reading, including streams with no known length, then validates content.
- The save picker selects a destination; it does not truncate or write it.
  `BeginWriteAsync(RequireAtomicReplace)` stages content without changing the target.
  Unsupported target/provider combinations return Unavailable before modification.
  No fallback to destructive truncation is permitted under that policy.
- `Content` is a staging stream. A single `CommitAsync` flushes staging and attempts
  the selected write policy. Disposing an uncommitted transaction removes staging.
  A successful atomic replacement means the destination receives the complete new
  content. It does not promise power-loss durability or cross-filesystem atomicity.
- `FileCommitResult` distinguishes `Committed`, `NotCommitted(code)` and
  `CommitUnknown(code)`. Cancellation before commit starts discards staging and
  throws. Once replacement starts, finish observing its result instead of reporting
  cancellation after a successful write. If the result cannot be determined, show
  that uncertainty and do not automatically retry an export.
- An explicit destructive write policy is deferred. Overwrite confirmation occurs
  in native UI, but is not a concurrency lock: if the destination changed after
  selection, fail with a conflict where that can be detected. Strong compare-and-swap
  guarantees on all filesystems are not part of the initial contract.

### Threading and shutdown

Callers may invoke OS services from bridge worker threads. Providers marshal native
work themselves; feature authors do not wrap every call in dispatcher code. Expose
`IUiDispatcher` for application-owned native extensions with short synchronous
callbacks. Do not initially expose an async-delegate dispatcher with ambiguous
continuation affinity. Native provider internals own asynchronous UI completions.

Dispatch executes inline when already on the owner thread. Cancellation before a
queued callback starts prevents execution; once it starts, report its actual outcome.
After shutdown starts, reject new work and settle queued work with an owner-lifetime
failure. Never block the UI thread waiting for a callback scheduled onto that thread.
Do not run application callbacks while holding host lifecycle or bridge locks.

One pending picker per owner is allowed in the first slice. A second request returns
`resource-busy` instead of opening another modal dialog or silently queuing forever.
Owner shutdown requests native dismissal, releases late-returned leases and prevents
late callbacks from reaching a replacement presentation. Native cleanup must not
depend on the owner still reporting `IsOpen`; the event pump must continue until
queued native release work has completed. The current macOS smoke hang is a specific
reason to establish this lifecycle invariant before adding owned sheets.

Clipboard reads happen only in explicit commands, never polling or reconnect.
Bound returned text while reading; writes validate their limit before dispatch.
After a native write has started, report whether it succeeded, even if cancellation
arrives concurrently. Linux clipboard persistence after application exit must be
measured per backend; a successful write promises availability while the provider
owns the selection, not unconditional survival after exit.

## Migration feature and frontend behavior

The application module receives `IFileDialogs` and `ITextClipboard` through DI.
The host composition supplies those services, including test providers. Keep the
reference's domain validation and persistence unchanged.

| User action | Runic command and result | Frontend responsibility |
| --- | --- | --- |
| Import contact | `ChooseContactImport` reads at most 4 KiB, parses and validates contact data; returns a candidate draft | Apply only to the original editor/draft generation; a file import does not save |
| Export contact | `ExportContact` snapshots a confirmed customer and writes JSON through a save transaction | Show the exported revision/name and distinguish dismissal, failure and unknown commit |
| Copy contact | `CopyContact` serializes the selected confirmed customer into bounded text | Announce actual success; do not claim success before native completion |
| Paste contact | `PasteContact` reads bounded clipboard text and returns a validated candidate | Apply with the same draft-generation check as import; preserve current draft on failure |

Record the originating editor and edit sequence when an interaction starts. If the
user edits or changes selection before import/paste returns, offer the candidate
for explicit application instead of overwriting the newer draft. Persistence still
uses the existing explicit save and revision checks. Export/copy operate on a
captured confirmed revision so concurrent edits cannot produce mixed content.

The bridge owns command receipts, operation state and cancellation. Reconnect may
recover an outstanding command's status; it must not replay a picker, clipboard
write or file commit. Process restart does not resume a native interaction. Scope
the service facade to the logical presentation, not the physical WebSocket.

The migration guide should map WPF `OpenFileDialog` and MAUI `FilePicker` usage to
the lease-based service, Toolkit async commands to Runic intents, dispatcher calls
to provider-owned dispatch, and property notifications to snapshots/events. This
is a migration guide for behavior; it does not preserve `ICommand`,
`INotifyPropertyChanged` or XAML bindings in the new feature.

## Delivery and decisions

1. Resolve current browser cleanup/native-loop failures and retain runner evidence.
2. Implement contracts, deterministic provider conformance and owner lifetimes in
   an internal prototype. Prove headless unsupported behavior and both-host DI.
3. Implement Windows and Linux providers and the customer import/export/copy/paste
   slice. Keep macOS marked unavailable until its Application main-thread runner,
   owned sheets and sandbox access are exercised on Apple Silicon.
4. Run package-only consumers with both hosts and both Desktop host profiles;
   measure the incremental native-provider footprint separately from the baseline.
5. Validate a MAUI-derived desktop document feature, then review public API and
   package boundaries. Mobile pickers/navigation require their own host milestone.

Review gates before public API: provider composition syntax, CS-WebUI native owner
access, Wayland parent export, per-target atomic-save support and clipboard lifetime.
The defaults in this RFC are decisions for the prototype, not reasons to block
implementation on a broad design committee. Record any change with its test evidence.

The planned scenario specification is in
[OS integration acceptance](../../../../eng/os-integration-acceptance.md).
