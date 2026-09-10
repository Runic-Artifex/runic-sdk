# NixOS portal test VMs (deprecated)

Use the [managed desktop container runner](container-automation.md) for Linux
portal, input, scaling and notification tests. These VM helpers remain available
for compatibility and explicit VM investigations; they are no longer the default
Linux workflow. Windows VM testing is unaffected.

The SDK flake provides two isolated graphical NixOS VMs for native portal
acceptance. Build exactly one desktop at a time:

```sh
nix build .#nixosConfigurations.runic-portal-kde.config.system.build.vm
./result/bin/run-runic-portal-kde-vm

# Or, in a separate invocation:
nix build .#nixosConfigurations.runic-portal-gnome.config.system.build.vm
./result/bin/run-runic-portal-gnome-vm
```

Each image logs in as `runic` automatically. The password is `runic` if a
desktop prompt needs it. The VM uses its own session bus and only installs the
desktop's upstream portal configuration. Plasma uses its `kde-portals.conf`,
including `plasmanotify` for notifications; GNOME uses its `gnome-portals.conf`
and the GTK fallback for interfaces GNOME does not export. The configured
backend and its version determine which native chooser is used. This makes a successful picker or notification
evidence for the selected desktop session rather than for the host session.

The image contains GTK 3, GTK 4, WebKitGTK 4.1, WebKitGTK 6.0 and D-Bus. It
mounts the Git-aware source snapshot supplied to `nix build` at
`/home/runic/src` read-only. The test command retains one bounded workspace on
the VM disk, refreshing it only when the mounted snapshot changes. It enters
the mounted flake's locked development shell for Bun, the .NET SDK and native
library and GSettings schema paths, then installs the frontend from the lockfile
on the first use.
The copied workspace is writable even though its source is immutable. The VM
registers the development shell's closure from the shared host store, and keeps
additional store writes on the guest disk instead of a small RAM-backed store.
Build outputs never modify the mounted source.

Open the desktop's terminal application inside the VM and run one check at a
time:

```sh
runic-portal-test settings
runic-portal-test native
runic-portal-test notifications
runic-portal-test open
runic-portal-test choose
runic-portal-test reveal
```

`notifications` registers the installed `com.runic.tests.Portal` desktop identity
and waits for the **Open result** action. Check notification history before its
120-second wait ends; the fixture removes its own notification after activation
or timeout. `open`, `choose` and `reveal` require manual confirmation after the
desktop UI handles the temporary result file.

The launchers use separate default disks, `runic-portal-kde.qcow2` and
`runic-portal-gnome.qcow2`, in the directory where each command runs. Set
`NIX_DISK_IMAGE` to an explicit path when a disposable test state is required.

The helper tests the snapshot that Nix captured. Stage intended source changes
before building a VM; do not use `path:.`, which would copy ignored caches and
build outputs into the Nix source snapshot. The VM has outbound user-mode network
access for initial NuGet restore but does not expose host services or reuse the
host desktop/session bus.

The normal commands exercise an unsandboxed desktop application. The
[Flatpak fixture](../../../tests/native/Runic.Desktop.Gtk4.Smoke/flatpak/README.md)
adds actual application-sandbox checks. See [VM automation](vm-automation.md)
for the unattended runner, current coverage and remaining desktop adapters.
Snap policy and production application packaging remain separate checks.

For command-driven testing, add `-serial stdio -monitor none` to the VM launcher
and log in as `runic` on the serial console. The graphical window remains
available for manual interaction. Import the guest desktop's display environment
before running native checks from that console:

```sh
export DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u)/bus"
while IFS='=' read -r name value; do
  case "$name" in
    DISPLAY|WAYLAND_DISPLAY|XAUTHORITY|XDG_CURRENT_DESKTOP|XDG_SESSION_TYPE)
      export "$name=$value" ;;
  esac
done < <(systemctl --user show-environment)
```

Use `sudo poweroff` in the guest to stop it cleanly. Preserve its disk to reuse
dependencies and build outputs, or remove a task-owned disposable disk after
retaining the logs you need.

## GTK4 and notification focus

`runic-portal-test gtk4` runs the GTK4/WebKitGTK 6 fixture: window lifecycle,
portal parent, clipboard, DesktopHost bridge, close veto/retry and reopen.
It checks that Wayland has focused the window before testing the clipboard.

The VM installs a second desktop identity, `com.runic.tests.Activation`, and a
session D-Bus activation service. Run `runic-portal-test activation-live` and
click **Open result** to test a running GTK4 receiver. The fixture logs whether
an activation token arrived and whether GTK reports the window active.

For cold activation, run `runic-portal-test activation-submit` and wait for its
successful exit. Then click **Open result**. The bus starts a different process
through the installed service; read `~/.cache/runic-activation-receive.log` and
`~/.cache/runic-activation-receipt`. Compare the receiver PID with the submitted
PID, and require `focused=True`, not just callback delivery. The service helper
uses the workspace built by submission, so the click does not require the first
restore/build. The test removes its notification after receiving the action.
No notification tokens are written to logs or receipts.

The test user and unlock password are both `runic`. GNOME test guests disable
idle locking by default so a wait for manual input does not hide notification
controls. In GNOME, hover over a notification to reveal **Open result**; clicking
the body invokes the separate default action and does not satisfy this test.

## GTK4 usability

`runic-portal-test gtk4-usability` opens the labelled input/IME, targeting, file
picker and inhibition fixture. GNOME includes IBus Intelligent Pinyin; KDE
includes Fcitx5 Pinyin with the Wayland frontend. Use actual compositor display
scales for scaling checks. `runic-atspi` runs the maintained native accessibility
inspector; `runic-portal-automate` drives the unattended checks described above.
The optional `--orca` automation records real screen-reader output for native
focus changes. Listening for announcement quality and visual candidate placement
remain separate from accessible-control and button-action assertions.
