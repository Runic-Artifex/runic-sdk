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
```

Every mode opens one embedded window (`ProbeWindow`): it checks that the
process is sandboxed (`APP_SANDBOX_CONTAINER_ID`), that WKWebView loaded the
page from Runic's loopback server and that the page answers over the bridge,
then hands the mode the surface, the window and the window's platform
services. A mode exits 0 when its checks pass and 1 otherwise; a wrong command
line exits 2.

`--sandbox-probe` needs no dialog:

- prints the window's capability snapshot (`CAPABILITY <id> <status>`) and
  checks that owned open dialogs are available and `platform.files.save`
  reports `Unavailable(AtomicReplaceUnavailable)`, the sandbox's missing
  atomic save of a chosen file (A1, M2 changes this), and prints what volume
  listing, login startup and open registration report (`SERVICE …`);
- saves `saved.txt` in the container's Application Support folder twice
  through a save lease and a staged write, with a picker that returns the file
  instead of showing a dialog;
- creates and resolves a security-scoped bookmark of it, and gets none for the
  file outside the container;
- opens a save lease for the file outside the container, which the sandbox
  refuses at some step; CI checks afterwards that the file kept its content.

It prints `SANDBOX_PROBE_OK` when all of that passed. A later mode adds its
own case to `Program.cs` and runs in `ProbeWindow.Run` as `SandboxProbe` does,
for example M2's `--document-probe`.

The probe runs only on macOS, from its signed bundle; elsewhere it exits 2.
The ad-hoc signature and the unsigned package stand in for the maintainers'
Apple Distribution and installer certificates, which CI does not have.
