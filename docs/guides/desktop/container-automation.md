# Linux desktop container automation

Managed systemd-nspawn is the preferred direction for Linux desktop integration
checks. GNOME's full Flatpak/input/audio sequence runs without QEMU or a host
Wayland, session D-Bus, PipeWire, home-directory or device bind. Plasma has a
separate configuration so each desktop selects its own portal implementations.
The Linux VM helpers are legacy fallback while the remaining container coverage
is completed; new Linux orchestration work belongs here.

## Prepare once

The host needs the active managed-nspawn helpers and BTF-enabled systemd described
in [the container configuration](../../../nixos/portal-container/README.md).
Prepare one small base directory, in a parent owned by the invoking user:

```sh
mkdir -p /tmp/runic-desktop-base
sudo install -d -o root -g root -m 0755 /tmp/runic-desktop-base/root \
  /tmp/runic-desktop-base/root/usr /tmp/runic-desktop-base/root/usr/bin
sudo systemd-dissect --shift /tmp/runic-desktop-base/root foreign
```

This is a one-time privileged filesystem preparation, not a per-desktop or
per-run system activation. The runner reuses it with `--volatile=yes`, which
creates fresh writable state and ignores the base's stored `/home` and `/etc`.
A private `/usr/bin` tmpfs lets normal NixOS activation create its compatibility
shims. Test-installed apps, preferences and logs disappear at shutdown after
results have been copied out. Do not use a real system root as this base.

Build the chosen desktop and locked Flatpak preparation tools from the SDK root:

```sh
nix build .#nixosConfigurations.runic-headless-gnome.config.system.build.toplevel \
  --out-link artifacts/container-gnome
nix build .#nixosConfigurations.runic-headless-kde.config.system.build.toplevel \
  --out-link artifacts/container-kde
nix build .#desktop-flatpak-tools --out-link artifacts/desktop-flatpak-tools
```

Prepare the standard runtime once, outside the network-isolated test. The helper
pins both GNOME Platform 50 and its Mesa GL extension and uses a dedicated cache;
it does not install anything into the host user's normal Flatpak installation.

```sh
PATH="$PWD/artifacts/desktop-flatpak-tools/bin:$PATH" \
  bash nixos/portal-container/prepare-runtime.sh "$PWD/.cache/container-flatpak" \
  > /tmp/runic-runtime-path
nix-store --add-root "$PWD/artifacts/container-runtime" --indirect \
  --realise "$(cat /tmp/runic-runtime-path)"
```

The runtime is shared read-only between test desktops. The nested Flatpak receives
its standard runtime, not the container's Nix store. Reuse the cache and GC root;
do not rebuild a runtime snapshot for every test invocation.

## Prepare fixture inputs

Build the [portable NativeAOT Flatpak fixture](../../../tests/native/Runic.Desktop.Gtk4.Smoke/flatpak/README.md)
using the locked SDK environment. For the native suite, use the ordinary
NativeAOT fixture publish. Put only the required executable and installer in a
small directory, then freeze that directory as the test input:

```sh
mkdir -p .cache/container-inputs
cp tests/native/Runic.Desktop.Gtk4.Smoke/flatpak/install.sh \
  .cache/container-inputs/install-flatpak.sh
cp artifacts/gtk4-flatpak/Runic.Desktop.Gtk4.Smoke \
  .cache/container-inputs/Runic.Desktop.Gtk4.Smoke.flatpak
# For the native mode, also copy its ordinary NativeAOT publish:
cp artifacts/gtk4-usability-aot/Runic.Desktop.Gtk4.Smoke .cache/container-inputs/
nix store add-path .cache/container-inputs > /tmp/runic-input-path
nix-store --add-root "$PWD/artifacts/container-inputs" --indirect \
  --realise "$(cat /tmp/runic-input-path)"
```

Omit `--flatpak` for the native suite. Recreate
the small input artifact after rebuilding a fixture; keep growing source trees,
SDK caches and build outputs out of Nix source snapshots.

## Run and collect

```sh
python3 -B nixos/portal-container/run.py \
  --desktop gnome --system artifacts/container-gnome \
  --root /tmp/runic-desktop-base/root \
  --inputs artifacts/container-inputs --runtime artifacts/container-runtime \
  --output artifacts/container-results/gnome-1 --flatpak --orca --keyboard
```

For Plasma, use `--desktop kde --system artifacts/container-kde` with
`--flatpak --orca`; the keyboard adapter currently supports GNOME only.

Use a new output directory for each run. The host launcher uses the **active host
systemd** tools, then launches the suite through the guest system manager as
`runic`. It waits for the actual desktop/display/Settings/PipeWire services,
retains `boot.log`, `session.log`, `suite.log`, `journal.log` and guest results,
and powers down its own machine on success or failure. Readiness and test
failures exit nonzero. Results are extracted with Python's safe data filter.
Run one desktop at a time initially.

GNOME checks cover accessible roles, WebView actions, native inhibition
registration/removal, portal grant/private-sibling denial, chooser cancellation,
atomic-write rejection preserving the destination, owner closure, real compositor
keyboard navigation and Pinyin composition, and Orca speech with captured output
from a private PipeWire null sink. The virtual keyboard remains alive for the
suite: removing the last input device from a headless seat drops focus. The
chooser uses verified native text entry and compositor Enter input.

Optional local Whisper validation uses the same
[speech verifier](vm-automation.md#orca-and-recorded-speech) against the copied
`results` directory. The container run checks actual Orca speech requests and
non-silent PCM independently; ASR does not replace those assertions.

## Remaining replacement work

Plasma also covers its native Qt chooser, Flatpak grants/cancellation/atomic-write
rejection, PowerDevil registration/removal and Orca/PipeWire audio. Its Qt
accessibility bridge must be enabled before inspecting dialogs; the runner sets
and restores the session accessibility status. PowerDevil is explicitly enabled
because NixOS normally omits power management in containers.

KDE/Fcitx5 compositor keyboard input remains to be automated. KWin exposes an
EIS RemoteDesktop connection suitable for a guest-only input adapter; no host
input-device bind is needed for that investigation. Notification live/cold action focus,
actual scale changes and pointer targeting also remain to be moved to the
container runners. Visual announcement/candidate quality and real hardware or
power transitions require separate assessment; a shared-kernel headless test
cannot establish those results. These are coverage limitations, not additional
release approval gates. Windows VM and future real-macOS testing are unchanged.
