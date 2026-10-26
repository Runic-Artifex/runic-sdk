# Sandboxed Runic Desktop probe

A minimal Runic Desktop application for the macOS App Sandbox. The packaging
job's macOS leg packages it with `dotnet runic package macos --pkg`; the
project sets `RunicMacSandbox` and `RunicMacApplicationCategory`, so the bundle
is ad-hoc signed with the sandbox entitlements and an unsigned installer
package is written beside the disk image. CI then checks the entitlements,
`LSApplicationCategoryType`, the 1024 px icon and the installer package's
payload, and runs the bundle's executable:

```sh
"Runic Sandbox Probe.app/Contents/MacOS/Runic.Sandbox.Probe" --sandbox-probe <file outside the container>
"Runic Sandbox Probe.app/Contents/MacOS/Runic.Sandbox.Probe" --document-probe
"Runic Sandbox Probe.app/Contents/MacOS/Runic.Sandbox.Probe" --document-reopen
```

Every mode opens one embedded window (`ProbeWindow`): it checks that the
process is sandboxed (`APP_SANDBOX_CONTAINER_ID`), that WKWebView loaded the
page from Runic's loopback server and that the page answers over the bridge,
then hands the mode the surface, the window and the window's platform
services. A mode exits 0 when its checks pass and 1 otherwise; a wrong command
line exits 2.

`--sandbox-probe` needs no dialog:

- prints the window's capability snapshot (`CAPABILITY <id> <status>`) and
  checks that owned open dialogs and `platform.files.save` are available (a
  chosen file saves through the item replacement directory, as
  `--document-probe` checks), and prints what volume listing, login startup
  and open registration report (`SERVICE …`);
- saves `saved.txt` in the container's Application Support folder twice
  through a save lease and a staged write, with a picker that returns the file
  instead of showing a dialog;
- creates and resolves a security-scoped bookmark of it, and gets none for the
  file outside the container;
- opens a save lease for the file outside the container, which the sandbox
  refuses at some step; CI checks afterwards that the file kept its content.

It prints `SANDBOX_PROBE_OK` when all of that passed.

`--document-probe` is a document app's save path in the sandbox (A1): it
bookmarks `documents/document.txt` in the container, opens it as a document
lease through `IDesktopFileBookmarks.OpenDocumentAsync`, saves it three times
(each write stages in `NSItemReplacementDirectory`, never beside the file, and
`replaceItemAtURL` replaces the file), checks that the first bookmark still
resolves after the replacements, and stores the lease's own bookmark from
`CreateBookmarkAsync`. It prints `DOCUMENT_PROBE_OK`. `--document-reopen`, a
second launch, reopens the document from the stored bookmark, reads the last
save, saves once more and removes both files; it prints `DOCUMENT_REOPEN_OK`.
A file the user chooses in a panel outside the container takes the same path
but needs a person to choose it, so it stays a manual check.

A later mode adds its own case to `Program.cs` and runs in `ProbeWindow.Run`
as `SandboxProbe` does.

The probe runs only on macOS, from its signed bundle; elsewhere it exits 2.
The ad-hoc signature and the unsigned package stand in for the maintainers'
Apple Distribution and installer certificates, which CI does not have.
