# NixOS portal test VMs

The SDK flake provides two isolated graphical NixOS VMs for native portal
acceptance. Build exactly one desktop at a time:

```sh
nix build .#nixosConfigurations.runic-portal-kde.config.system.build.vm
./result/bin/run-runic-portal-vm

# Or, in a separate invocation:
nix build .#nixosConfigurations.runic-portal-gnome.config.system.build.vm
./result/bin/run-runic-portal-vm
```

Each image logs in as `runic` automatically. The password is `runic` if a
desktop prompt needs it. The VM uses its own session bus and only installs the
portal backend for its selected desktop: KDE Plasma uses
`xdg-desktop-portal-kde`; GNOME uses `xdg-desktop-portal-gnome`. This makes a
successful picker or notification evidence for the selected backend rather than
for the host session.

The image contains GTK 3, GTK 4, WebKitGTK 4.1, WebKitGTK 6.0, D-Bus and the
same .NET SDK major version as the development flake. It mounts the Git-aware
source snapshot supplied to `nix build` at `/home/runic/src` read-only. The test
command copies that snapshot to the VM disk before restore/build, so build
outputs never modify the mounted source.

Open Konsole inside the VM and run one check at a time:

```sh
runic-portal-test settings
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

The helper tests the snapshot that Nix captured. Stage intended source changes
before building a VM; do not use `path:.`, which would copy ignored caches and
build outputs into the Nix source snapshot. The VM has outbound user-mode network
access for initial NuGet restore but does not expose host services or reuse the
host desktop/session bus.

These VMs exercise an unsandboxed desktop session. Flatpak/Snap portal policy,
real application packaging and notification cold relaunch remain separate tests.
