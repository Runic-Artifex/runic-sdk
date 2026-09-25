# OS integration acceptance scenarios

These scenarios cover the independent Platform runtime and native OS providers.
The [release policy](release/README.md) describes publication, and the
[platform runtime test guide](../tests/dotnet/Runic.Platform.Runtime.Tests/README.md)
gives the runnable commands. Record the OS, desktop session, provider, and result
when gathering interactive evidence.

## Automated conformance

The headless `Runic.Platform.Runtime.Tests` run checks provider selection,
readiness, owner generations, resource leases, dispatch, and Desktop services.
CI runs it in the managed suite and publishes and executes the same project with
NativeAOT on Linux x64, Windows x64, and macOS arm64. Native clipboard and portal
tests run separately in the native matrix.

Use deterministic providers to test completion, cancellation, owner replacement,
dispatch, and commit boundaries. A settled task alone does not prove that leases
were released or side effects happened only once.

| ID | Trigger / setup | Required observable outcome |
| --- | --- | --- |
| CAP-01 | No provider configured | Readiness reports an unavailable reason without loading native UI. |
| CAP-02 | Owner is not created or has closed | Readiness follows the current owner generation; stale snapshots cannot start an operation. |
| CAP-03 | Provider cannot verify the required native parent | Owned operation reports owner unavailable. |
| PICK-01 | User dismisses a picker | Result is dismissed and the owner remains usable. |
| PICK-02 | Cancel before UI dispatch | No native dialog opens and no resource is acquired. |
| PICK-03 | Cancel during selection, followed by late completion | Completion settles once and a late lease is disposed. |
| PICK-04 | Two pickers request one owner | Only one starts; a separate owner can proceed. |
| PICK-05 | Close or replace an owner during selection | Old callbacks cannot affect the replacement window. |
| FILE-01 | Read a selected resource with unknown length | Enforce the byte limit while reading; dispose streams and leases on every path. |
| FILE-02 | Selection disappears or access fails | Return a stable failure and release acquired access. |
| SAVE-01 | Dispose staged output without committing | Original destination remains unchanged. |
| SAVE-02 | Cancel before or after replacement starts | Report the actual committed result and remove unused staging. |
| SAVE-03 | Commit outcome is unknown | Preserve the unknown result; do not retry automatically. |
| CLIP-01 | Clipboard is empty, busy, denied, or changes owner | Report the corresponding result and allow a later explicit retry. |
| THREAD-01 | Invoke a provider off the owner thread | Native work uses its required dispatcher without deadlock. |
| THREAD-02 | Cancel queued work or close the owner | Callback does not execute after cancellation; shutdown settles queued calls. |

## Native evidence

The ordinary test run does not open a window. Use `--native-services` for live
launcher and notification checks, and `--native-select` for a selected-resource
read. These are manual checks: CI cannot establish that a human approved a
dialog or that a compositor supplied the required permissions. The selection
probe reads at most 4096 bytes and does not print file contents.

| Target | Required environment | Live checks |
| --- | --- | --- |
| Windows x64 | Interactive user session with WebView2 | Owned file selection, cancellation, clipboard, launcher, and notification activation. |
| Linux x64 X11 | GTK/X11 session, session bus, portal backend when selected | Parent identity, selection and access lifetime, clipboard ownership, and portal availability. |
| Linux x64 Wayland | Supported compositor and portal backend | Owner token, modality, selection lifetime, clipboard ownership, and owner replacement. |
| macOS arm64 | AppKit user session | Owned selection, cancellation during closure, clipboard, launcher, and notifications. |
| macOS sandboxed app | Signed fixture with file entitlements | Selected-resource access and balanced release; follow the [sandbox instructions](../tests/native/platform-sandbox/README.md). |

Record a reproducible manual run with OS and backend versions when a CI runner
cannot provide an interactive session. A browser file input or simulated provider
does not count as native picker evidence.

The macOS clipboard implementation uses ApplicationServices C Pasteboard APIs
and CoreFoundation. Linux uses GTK `UTF8_STRING`; managed reads are bounded after
native selection transfer, but GTK native transfer allocation is not bounded by
that limit. Native ownership does not guarantee clipboard persistence after exit.
