# OS integration acceptance scenarios

> Historical first-preview scope. Its acceptance gates are retired for future
> SDK releases; see the [current release policy](release/README.md).

These scenarios exercise the shared platform contracts, runtime, three OS providers
and presentation-scoped application integration. They support the
[API RFC](../docs/guides/application/architecture/os-integration-rfc.md) and define
both current preview acceptance and broader validation before v1. Source implementation
alone does not establish native certification or registry publication.

For `0.2.0-preview.1`, the [demo-preview policy](preview-human-acceptance.md) requires
manual checks on this Linux system and the available Windows VM, plus automated
native JIT/NativeAOT CI on all three OS targets. Real macOS/sandbox checks,
unavailable Wayland, broader accessibility and independent pilots are deferred before
v1 and do not block this demo preview. Retain their pending status without synthetic
passes. Every receipt must identify the tested source and artifact hashes.

The macOS clipboard implementation uses ApplicationServices C Pasteboard APIs and
CoreFoundation, not `NSPasteboard`. Linux uses GTK `UTF8_STRING`; managed reads are
bounded after native selection transfer, but GTK native transfer allocation is not
bounded by that limit. Transfer failures lack a separate GTK permission code and
report `IoError`; successful ownership does not guarantee persistence after exit.

## Application references

The [customer migration](../examples/customer-migration/README.md) exercises native
contact import/export and copy/paste while sharing domain and persistence rules
with its WPF baseline. Use the real member bridge and generated frontend contracts.
A browser HTML picker remains a separate presentation choice, not native evidence.

The [document migration](../examples/document-migration/README.md) exercises open,
edit, save, cancellation and dirty-close protection with a MAUI-derived portable
baseline. It does not certify a compiled MAUI application, Android or iOS. Its 32 KiB
UTF-8 document policy is separate from the customer's 4 KiB JSON input policy.
Compare only behavior actually covered by each baseline; new functionality needs
its own acceptance scenarios.

## Deterministic provider and application scenarios

Test fixtures control completion, cancellation, owner replacement, dispatch and
commit boundaries using signals. Avoid relying on sleep durations to hit a race.

| ID        | Trigger / setup                                                          | Required observable outcome                                                                               |
| --------- | ------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------- |
| CAP-01    | No native provider configured                                            | Manifest support and scoped readiness report explicit unavailable reasons; no UI or native library loaded |
| CAP-02    | Provider configured, owner not created / subsequently closed             | Current readiness changes with owner generation; stale snapshots do not let an operation proceed          |
| CAP-03    | Owner required but provider has no verified parent identity              | Unavailable with `owner-unavailable`; no silently unowned dialog                                          |
| CAP-04    | No verified native owner is available                                    | Owned picker reports unavailable; unowned dialogs are outside this preview contract                       |
| PICK-01   | User dismisses open or save picker                                       | Dismissed result; draft and destination unchanged; owner is usable afterward                              |
| PICK-02   | Caller cancels before UI dispatch                                        | No native dialog opens and no resource is acquired                                                        |
| PICK-03   | Caller cancels while picker is open, followed by a late native selection | Dismiss picker, settle cancellation once, dispose late lease, do not apply data                           |
| PICK-04   | Two pickers requested for the same owner                                 | One opens; second reports busy; another independent owner can proceed                                     |
| PICK-05   | Close/replace owner while picker is pending                              | Cleanup completes; old callback cannot affect replacement window or feature state                         |
| FILE-01   | Valid JSON import up to 4096 bytes                                       | Candidate draft matches baseline domain parsing; no persistence before explicit save                      |
| FILE-02   | 4097 bytes, malformed UTF-8/JSON, wrong shape or invalid fields          | Typed error; previous draft and database unchanged; no partial candidate                                  |
| FILE-03   | Stream has unknown length or grows while being read                      | Enforce byte limit during reads; do not trust metadata                                                    |
| FILE-04   | Read fails / access denied / resource disappears after selection         | Stable failure; every acquired stream and lease disposed                                                  |
| FILE-05   | Dispose lease with outstanding stream, then use stream/open again        | Stream closes, new access fails, repeated disposal is harmless                                            |
| FILE-06   | Simulated platform access acquisition and release                        | Release each SDK-acquired access exactly once, including failures and cancellation                        |
| SAVE-01   | Pick existing file, stage output, then dispose without commit            | Original bytes unchanged; staging removed                                                                 |
| SAVE-02   | Atomic replacement unsupported for selected destination                  | Unavailable before modifying destination; no destructive fallback                                         |
| SAVE-03   | Stage valid output and commit                                            | Complete exported JSON equals the captured confirmed customer revision                                    |
| SAVE-04   | Cancel immediately before commit starts                                  | Original destination unchanged; staging removed; cancelled outcome                                        |
| SAVE-05   | Cancel after irreversible replacement starts                             | Report actual committed/not-committed result; never report cancelled after confirmed success              |
| SAVE-06   | Inject failure whose commit outcome cannot be determined                 | CommitUnknown reaches application/UI; no automatic retry or false success                                 |
| SAVE-07   | Destination changes after selection                                      | Detect supported conflicts and preserve destination; document filesystem limits                           |
| CLIP-01   | Clipboard has no text, empty text, malformed contact, or oversized text  | Distinct no-text/empty behavior; validation/size failure preserves draft                                  |
| CLIP-02   | Clipboard busy or permission denied                                      | Typed failure; no success notification; subsequent explicit retry can succeed                             |
| CLIP-03   | Cancel before dispatch / after native write starts                       | No write before dispatch; once started, report actual outcome                                             |
| THREAD-01 | Invoke services from a bridge worker thread                              | Native work executes on correct dispatcher; bridge remains responsive                                     |
| THREAD-02 | Invoke dispatcher from owner thread                                      | Callback runs inline without deadlock                                                                     |
| THREAD-03 | Cancel queued callback, then pump queue                                  | Callback does not execute; completion settles once                                                        |
| THREAD-04 | Close native window with cleanup still queued                            | Pump continues until native release completes; shutdown rejects new work and settles queued calls         |
| APP-01    | Edit/change selection while import or paste waits                        | Late candidate cannot overwrite newer draft; explicit apply remains possible                              |
| APP-02    | Edit/save while export or copy waits                                     | Output contains one captured confirmed revision, never mixed state                                        |
| APP-03    | Disconnect/reconnect during picker, copy or commit                       | Operation status recovers; side effect is not replayed; draft is preserved                                |
| APP-04    | Replace presentation during reconnect                                    | Old service owner and callbacks rejected; new owner has its own generation                                |

For cancellation races, record provider call count, lease count and destination
hashes as well as command results. A settled promise alone does not prove cleanup
or absence of a duplicate side effect.

## Live frontend acceptance

Run the same scenario driver against Desktop default, Desktop minimal and
CS-WebUI with a real application bridge. Use deterministic providers for race
coverage; label those tests as provider simulations. Separate native tests must
exercise actual platform dialogs and clipboard.

1. Import a contact using the native service, inspect the populated draft, save,
   reconnect and verify persisted state against the original feature's baseline.
2. Dismiss and cancel import; verify draft, focus and persisted state are unchanged.
3. Edit while import is pending; verify the late candidate is offered rather than
   applied automatically.
4. Export a confirmed contact; read the produced file independently and compare
   its decoded content to the captured revision. Exercise overwrite, failure and
   unknown-commit feedback.
5. Copy and paste a contact through an independently observed clipboard; verify
   actual content, maximum size and no clipboard reads during reconnect.
6. Repeat an interaction after cancellation and after an OS error to prove that
   ownership/dispatch gates were released.
7. Navigate the flows by keyboard; restore focus to the originating control and
   announce success/error accessibly. Record manual screen-reader results separately.

Assert feature outcomes and host selection, not screenshots of OS-specific dialog
layouts. Do not count a browser `<input type="file">` or mocked provider as native
picker evidence. Run fake and real providers through the same contract checks where
possible, while keeping their evidence labels distinct.

## Native platform evidence

| Target                      | Required environment                                                         | Required live checks                                                                                                       |
| --------------------------- | ---------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| Windows x64                 | Interactive user session; installed browser and embedded WebView2 paths      | Owned Common Item Dialog, dismissal/cancellation, STA/UI responsiveness, clipboard busy handling, overwrite/atomic commit  |
| Linux x64 X11               | GTK/X11 session; session bus and portal backend when portal mode is selected | Parent identity, selection/access, text clipboard and lifetime; portal selection and missing-portal result when configured |
| Linux x64 Wayland           | Actual supported compositor and portal backend                               | Exported owner token, modality/focus, selection lifetime, clipboard ownership and owner replacement                        |
| macOS arm64                 | Main-thread Application runner; AppKit session                               | Owned sheets, cancellation during closure, cleanup completion, clipboard, file access                                      |
| macOS sandboxed application | Signed sandboxed fixture with declared file entitlements                     | Selected-resource access and balanced release; no retained-bookmark claim in first slice                                   |

Hosted runner availability does not establish that every native UI interaction is
automatable there. Where CI cannot provide a required session, retain a reproducible
manual run with OS/backend versions and mark certification pending until it exists.
Do not silently skip and label the target passed. Nix provides build/runtime
libraries; a portal daemon, session bus, compositor and interactive permissions are
runtime conditions to probe, not implied by entering `nix develop`.

## Package and footprint gates

- Packed consumers build outside the checkout with isolated caches. Both host
  selections use identical domain/application/frontend source and generated contracts.
- A CS-WebUI consumer must not resolve Runic Desktop or ASP.NET Core. Optional OS
  providers must not enter the dependency graph when omitted.
- NativeAOT publish succeeds with warnings treated as errors on all supported RIDs.
  Keep provider selection static and audit generated/native bindings for trimming.
- Run the existing `runic size` harness on the same baseline with providers disabled
  and enabled. Report delta, native dependencies, compressed distribution and exact
  source/package hashes; do not merge these numbers into the earlier baseline.
- Keep default and minimal Desktop conformance intact. OS services must not require
  features omitted by the minimal ASP.NET Core builder.

## Remaining validation before v1

Complete real macOS sheets/clipboard and signed sandbox selected-file access,
Wayland/portal checks on an actual supported session, broader NVDA/VoiceOver/Orca,
IME/high-contrast/scaling profiles, and two independent developer pilots. Repeat
affected flows against the final artifacts after fixes. Track unresolved defects in
[v1 priorities](../docs/guides/application/architecture/v1-follow-up-priorities.md).

Keep public API and native interop review, optional-provider dependency isolation,
conflict/uncertain-commit feedback, owner replacement and concurrent shutdown under
regression coverage. No count of passing simulated tests replaces real interaction.
Publication follows the current [release tooling](release/README.md) and
[candidate acceptance policy](preview-human-acceptance.md).
