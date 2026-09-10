# NixOS portal test VMs

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
and the GTK fallback for interfaces GNOME does not export, including the file
chooser and notifications. This makes a successful picker or notification
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

These VMs exercise an unsandboxed desktop session. Flatpak/Snap portal policy,
real application packaging and notification cold relaunch remain separate tests.

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
