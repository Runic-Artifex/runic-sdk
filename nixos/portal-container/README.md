# Headless desktop container experiment

This is an opt-in feasibility probe, not a replacement for the desktop VM suite.
The full GNOME session, Settings portal, PipeWire and virtual display now pass
the session probe both in the disposable VM and in managed nspawn directly on
the host. Native Runic controls and WebView
actions also pass there; inhibition currently returns `BackendUnavailable`.
Build using the SDK's locked Git-aware flake:

```sh
nix build .#nixosConfigurations.runic-headless-gnome.config.system.build.nspawn \
  --out-link artifacts/runic-headless-gnome
```

The launcher name is `bin/run-runic-headless-gnome-nspawn`. Run it in a disposable
NixOS VM with the same store available, from an empty task-owned working directory:

```sh
sudo systemd-run --unit=runic-headless-probe --property=Delegate=yes \
  --working-directory=/absolute/task-owned/directory \
  /absolute/path/to/bin/run-runic-headless-gnome-nspawn \
  --register=yes --console=pipe
```

The locked upstream test launcher shares the read-only Nix store, uses
`--private-users=no`, and exposes parent proc/sys under `/run/host` for its test
infrastructure. It is intended here for trusted test code inside a disposable
VM. Do not treat it as a hardened host container or pass host desktop sockets
into it. Host managed/unprivileged nspawn support is a separate configuration
workstream. The container has no external network, physical GPU, or host home
binding. Its disposable `runic` account has password `runic`.

For commands in the registered container:

```sh
sudo systemd-run -M runic-headless-gnome --uid=runic --wait --pipe \
  /run/current-system/sw/bin/env XDG_RUNTIME_DIR=/run/user/1000 \
  DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus \
  /run/current-system/sw/bin/gdbus call --session \
  --dest org.gnome.Mutter.DisplayConfig \
  --object-path /org/gnome/Mutter/DisplayConfig \
  --method org.gnome.Mutter.DisplayConfig.GetCurrentState
```

On 2026-09-10, booted inside the GNOME test VM, this configuration ran GNOME
Shell 50.4 with llvmpipe, its own `/run/user/1000/runic-wayland` socket and a
1280×800@60 virtual monitor reported by Mutter. Its session bus ID differed from
the parent desktop's bus. PipeWire was active. Enabling `hardware.graphics`
was necessary: without Mesa drivers, Clutter could not initialize its renderer.
No parent Wayland, session D-Bus or PipeWire socket was shared.

Starting Shell alone originally left `org.gnome.SessionManager` absent and
portal activation failed on a session dependency. The configuration now starts
`gnome-session --session=gnome`, overriding only its Shell service to select the
headless backend. It preserves normal GNOME session and portal dependencies.
The packaged read-only probe verifies `IsSessionRunning`, a virtual monitor,
the Settings portal and active PipeWire:

```sh
nix build .#nixosConfigurations.runic-headless-gnome.config.system.build.runicSessionProbe \
  --out-link artifacts/runic-session-probe
```

Execute `bin/runic-session-probe` inside the container as `runic`, with
`XDG_RUNTIME_DIR=/run/user/1000` and
`DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus`. It prints JSON and fails
nonzero if a required service or virtual monitor is absent.

The ordinary native usability runner accepts this container's hostname. Build
`config.system.build.runicContainerFixture` from the same configuration for a
launcher that supplies ICU, OpenSSL and GTK/WebKit runtime libraries. Its first
argument is the absolute path to the NativeAOT executable. The container enables
NixOS's `nix-ld` support, preserving the executable identity used by GTK/AT-SPI.
Keep the executable's `Runic.Desktop.Gtk4.Smoke` name for the runner's lookup.
When using `systemd-run`, specify `--working-directory=/home/runic` and
`PATH=/run/current-system/sw/bin`; launching from `/` makes .NET's configuration
watcher traverse the filesystem, including the shared store.

Native controls and WebView snapshot/action assertions passed on 2026-09-10.
The same run reached a real limitation: inhibition returned `BackendUnavailable`.
Do not count the full usability suite as passed. Investigate headless
session/inhibition semantics, then test pickers, notifications, IME and audio.
Add a separate Plasma configuration and repeat before claiming parity.
Nested Flatpak grants and WebKit sandbox behavior require their own execution;
a running compositor does not establish those results.

Stop the task-owned container after collecting diagnostics:

```sh
sudo systemctl stop runic-headless-probe
```

## Managed containers directly on the host

The host must supply active `systemd-nsresourced` and `systemd-mountfsd` sockets,
with systemd's BTF-enabled user-namespace guard. Merely enabling the sockets was
insufficient with the locked package: its automatic header detection cannot read
the live kernel inside a Nix build. The host configuration now derives the header
from its selected kernel package. The resulting systemd build reports `+BTF`.

Managed directory roots need foreign UID ownership. Prepare a task-owned root
once with administrator credentials, then launch unprivileged:

```sh
sudo install -d -o root -g root -m 0755 /absolute/test-root \
  /absolute/test-root/usr /absolute/test-root/usr/bin
sudo systemd-dissect --shift /absolute/test-root foreign
containerSystem=$(nix build --no-link --print-out-paths \
  .#nixosConfigurations.runic-headless-gnome.config.system.build.toplevel)
systemd-nspawn --user --directory=/absolute/test-root \
  --private-users=managed --private-users-ownership=foreign \
  --private-network --bind-ro=/nix/store --register=no \
  --machine=runic-managed-gnome --console=interactive "$containerSystem/init"
```

Use a new, empty task-owned path. The initial directories must be root-owned
before shifting: `systemd-dissect --shift` preserves relative ownership, so a
user-owned root remains user-owned inside the container. This makes systemd
reject runtime directory provisioning as an unsafe ownership transition.

`--private-users-ownership=map` does not provide the required mapping in managed
mode. The explicit foreign mode maps the on-disk ownership into the allocated
namespace. On 2026-09-10, this path booted directly on the host, accepted ordinary
`runic` login and passed the packaged GNOME session/display/Settings/PipeWire
probe. The initial user-owned root caused post-authentication login failure and
missing graphics drivers; correcting only the initial directory owners fixed
both without changing PAM or sandbox settings. No host display/session sockets
are bound.

The NativeAOT fixture then exposed a separate managed-container limitation:
WebKit's nested bubblewrap sandbox failed with `Can't mount proc on
/newroot/proc: Operation not permitted`, and its D-Bus proxy could not start.
Thus native application parity on the managed host is **not** established.
Investigate the nested PID/proc mount requirements before running Flatpak there.
Do not disable WebKit's sandbox or expose the host's proc/session mounts merely
to make the test pass. The VM-contained launcher has a different privilege/proc
model; its partial native success does not prove managed-host equivalence.

The locked NixOS Python driver also supports nspawn nodes. A focused host run
booted successfully inside the Nix build sandbox, but SUID wrapper creation
failed there and the user manager could not establish PAM authentication.
Do not disable PAM or application sandboxes to turn that result green. Treat
managed execution outside the build sandbox as a separate runner candidate.

Retain VM jobs for boot/login, seat and
virtual device behavior, power transitions, and kernel-dependent isolation.
See the [systemd desktop session model](https://github.com/systemd/systemd/blob/main/docs/DESKTOP_ENVIRONMENTS.md)
and [PyGObject headless Mutter testing](https://gnome.pages.gitlab.gnome.org/pygobject/guide/testing.html).
