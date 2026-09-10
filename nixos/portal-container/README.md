# Headless desktop container experiment

This is an opt-in feasibility probe, not a replacement for the desktop VM suite.
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

The experiment also exposed a real gap: starting Shell alone leaves
`org.gnome.SessionManager` absent, and portal activation fails on a session
dependency. The current configuration intentionally preserves that result.
Next, provide the proper GNOME session lifecycle, then test portal settings,
pickers, notifications, inhibition, AT-SPI, IME and audio against the same native
runner. Add a separate Plasma configuration and repeat before claiming parity.
Nested Flatpak grants and WebKit sandbox behavior require their own execution;
a running compositor does not establish those results.

Stop the task-owned container after collecting diagnostics:

```sh
sudo systemctl stop runic-headless-probe
```

NixOS's locked Python test driver supports both VM and nspawn nodes, so a shared
test harness is a plausible next step. Retain VM jobs for boot/login, seat and
virtual device behavior, power transitions, and kernel-dependent isolation.
See the [systemd desktop session model](https://github.com/systemd/systemd/blob/main/docs/DESKTOP_ENVIRONMENTS.md)
and [PyGObject headless Mutter testing](https://gnome.pages.gitlab.gnome.org/pygobject/guide/testing.html).
