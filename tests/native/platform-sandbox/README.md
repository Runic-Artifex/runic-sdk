# macOS selected-file access fixture

The native CI job retains an ad-hoc signed sandboxed NativeAOT app. Signing is
preparation for a manual test; it does not count as a user permission grant.

Extract the retained tarball to preserve executable permissions.
On Apple Silicon macOS, create a small text file outside the application's
container, then run the retained app from Terminal:

```sh
RunicPlatform.app/Contents/MacOS/Runic.Platform.Prototype.Tests --native-select
```

Select that file in the owned open sheet. The test reads at most 4096 bytes without
printing content, releases the acquired lease, then exercises open-sheet
cancellation and owner closure during a save sheet. Cancellation/dismissal of the
manual selection fails the evidence run; it is not recorded as granted access.
The native watchdog permits four minutes for this manual run.

Record the OS version, binary commit/hash, codesign entitlement output, selection
location relative to the sandbox, and complete test log. Repeat with a revoked or
inaccessible resource and record the typed permission failure. Confirm the sheet
is attached to the app window and focus returns after cancellation. Do not record
private file content. The provider balances only security access it starts; it
makes no retained-bookmark or general filesystem permission promise.

For an ordinary desktop-session selection test, the same `--native-select` flag
works with the Windows/Linux/macOS prototype executable outside a sandbox. Linux
Wayland/portal evidence additionally needs a supported compositor and portal;
Xvfb cancellation is not a substitute.
