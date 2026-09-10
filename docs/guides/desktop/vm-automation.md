# Desktop VM automation

The first unattended GTK4 runner is implemented in
`tests/native/Runic.Desktop.Gtk4.Smoke/automate-usability.py`. It drives the real
native AT-SPI tree and checks the fixture's observed results. It does not inject
DOM events, mock portal responses, or disable accessibility/WebKit isolation.

## Run in a disposable Linux guest

Build a [portal VM](portal-vm.md) from the current source and run from its graphical
terminal. For serial access, import the desktop environment as described there.
Use a new output directory for each invocation; close other Runic fixtures first.

```sh
runic-portal-automate --output "$HOME/.cache/runic-automation/basic-1" \
  -- runic-portal-test gtk4-usability
```

The runner launches and owns its fixture process, waits for readiness, enforces
an overall ten-minute deadline, and terminates its process group on failure.
`--startup-timeout` (default 300 seconds) accommodates the initial build/restore.
It writes `fixture.log`, `accessibility.json`, and `results.json`; failures also
include a traceback. A failed or timed-out assertion exits nonzero. Outputs are
diagnostic artifacts, not a new release approval requirement.

The common sequence checks accessible control names, a native button action
reaching the WebView, inhibition acquisition/release, and invalidation of a
pending picker when its owner closes. In GNOME it additionally observes the
actual SessionManager inhibitor appearing and disappearing. This verifies a
registered request, not whether the machine physically suspends.

For the standard-runtime Flatpak fixture, follow its
[installation instructions](../../../tests/native/Runic.Desktop.Gtk4.Smoke/flatpak/README.md), then run:

```sh
runic-portal-automate --output "$HOME/.cache/runic-automation/flatpak-1" \
  --gnome-pickers -- flatpak run --user com.runic.tests.Sandbox
```

`--gnome-pickers` currently targets the English Nautilus chooser in the locked
GNOME guest. It navigates through native accessibility interfaces, selects the
fixture file, cancels another request, and accepts the save confirmation. It
checks granted document contents, denied access to the private host sibling,
`AtomicReplaceUnavailable`, unchanged destination bytes, and no extra siblings.
It fails if the sandbox denial check did not run. It only uses the disposable
`~/runic-sandbox-inputs` files created by the installer.

On 2026-09-10 this entire sequence passed in the GNOME Wayland VM using NativeAOT
and `org.gnome.Platform/x86_64/50`, runtime commit
`545da92354a265d2c3572c91c39ac14dd7e74f9d8f9b66744ad50f478d2497c5`.
The first automated run reused the interactive test VM; clean-image repeatability
and the KDE adapter remain follow-ups. Do not infer either from this result.

## Extending the suite

Use the NixOS Python test driver for Linux boot, login, guest commands, QEMU
keyboard/mouse input, screenshots and log collection. The pinned nixpkgs already
contains GNOME and Plasma test examples. Reuse our desktop modules, with separate
KDE/GNOME jobs, rather than automating a maintainer's desktop or installing both
portal stacks in one guest. Prepare the fixture and dependencies before the test
phase; downloads and builds should not consume an interaction timeout. Keep the
standard Flatpak runtime pinned and available to the guest without granting
access to the host Nix store.

| Area | Automation approach and next assertion |
| --- | --- |
| VM lifecycle | Add a NixOS test-driver entry point around the guest runner; boot to Wayland, collect artifacts even on failure, shut down only its own guest. Run one desktop at a time initially. |
| KDE pickers/inhibition | Add an adapter from the actual Qt accessibility tree and observe PowerDevil's native inhibition state. Require the same grant/save/cancel assertions as GNOME. |
| Keyboard and IME | Send QEMU keyboard events through IBus/Fcitx5; assert real composition events and committed text in the fixture. Changing an accessible text value does not test an IME. |
| Scaling and targeting | Set actual Mutter/KScreen display scales, read them back, use QEMU pointer input at measured control bounds, and verify hit counts. Retain screenshots for caret/candidate placement. CSS zoom and an AT-SPI button action do not establish physical targeting. |
| Notifications | Activate the visible notification action through the shell UI. Assert the receiver PID and native focused-window result for both live and cold launch. Calling the application's D-Bus callback directly would bypass the activation-token behavior under test. |
| Accessibility | Extend native assertions to roles, values, focus order and events; capture Orca/speech-dispatcher output for regressions. A spoken usability check remains useful for announcement quality. |
| Windows | Use the existing interactive VM login and Windows UI Automation for WebView2/WinUI dialogs, with the same fixture outcomes and isolated temporary files. Run executable-only NativeAOT publishes and the existing native power-request checks. Session-0 SSH alone cannot cover interactive display behavior. |
| macOS | Add an AXUIElement/Accessibility adapter and native assertions after the real Mac is available. Keep native support explicitly untested until then. |

Start these as focused, opt-in VM jobs. Move stable scenarios into CI after a
fresh-image run establishes their dependencies and failure handling. Preserve
structured results, native logs and failure screenshots; do not add broad soaks,
mandatory manual gates or retry failures until they happen to pass. Visual IME
placement, spoken quality and real power transitions are still outside the
current automated runner's claims.
