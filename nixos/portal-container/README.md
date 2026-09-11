# Isolated Linux desktop test containers

Managed systemd-nspawn is the preferred Linux integration runner. The separate
GNOME and Plasma configurations start real desktop sessions with independent
Wayland displays, session buses, portal backends and PipeWire instances. They
use software rendering and have no host desktop sockets, home, physical devices
or external network bindings. See the [container automation guide](../../docs/guides/desktop/container-automation.md)
for preparation, builds, execution and collected results.

`run.py` owns the container lifecycle. It uses the activated host's managed-nspawn
support, a prepared foreign-owned base, and fresh volatile state for each run.
Only the immutable Nix store, selected fixture inputs and optional Flatpak runtime
are shared read-only. GNOME uses Mutter's headless backend; Plasma uses KWin's
virtual backend. Service drop-ins preserve the normal desktop session dependencies.
The disposable account is `runic`, password `runic`.

## Host prerequisites

The host needs active `systemd-nsresourced` and `systemd-mountfsd` sockets and
systemd's BTF-enabled user-namespace guard. On the development host this is
configured in `/home/viktor/my-flakes`: the BTF header is derived reproducibly
from the selected kernel package. Merely enabling the sockets was insufficient;
the activated systemd build must report `+BTF`.

Prepare the base once as described in the guide. Its parent must belong to the
invoking user; the root and initial `usr/bin` directories must be root-owned
before `systemd-dissect --shift ... foreign`. Relative ownership is preserved by
shifting, so shifting a user-owned root does not create a usable guest root.
The runner verifies foreign-root ownership and uses `--private-users=managed`
with `--private-users-ownership=foreign`. No per-desktop system activation or
privileged preparation is required afterward.

Use the host's activated systemd tools rather than the SDK shell's unpatched
package. Commands in the guest run through its system manager as `runic`, with a
home working directory. Enter the guest PID namespace as well as its mount/user
namespaces: retaining a host PID with a guest `/proc` view produces misleading
portal and sandbox failures.

## Nested application sandboxes

WebKit and Flatpak keep their bubblewrap sandboxes enabled. Managed nspawn's
masked proc initially prevented WebKit from mounting a nested PID namespace's
proc. `runic-pristine-proc.service` creates an auxiliary proc for the **guest PID
namespace** at `/run/runic-proc-private/proc`, beneath a root-owned `0700`
directory, with `nosuid,nodev,noexec`. The test user cannot traverse it. This
satisfies the kernel's proc visibility check without exposing host proc.
`PrivateMounts=false` keeps this mount in the guest namespace for nested sandboxes.

The service also binds only that proc's `sys/user` directory onto guest
`/proc/sys/user`. Flatpak's `--disable-userns` needs to lower its own nested user
namespace limit; nspawn otherwise masks this directory read-only. These limits
are resolved against the caller's current user namespace by the kernel, not the
host's global namespace. A before/after test lowered the limit in a nested user
namespace and verified its parent's limit remained unchanged. Other nspawn proc
masks remain in place. Both mounts are removed on service shutdown.

The standard-runtime Flatpak tests verify granted document access while direct
access to the private sibling remains denied. They also reject atomic replacement
without changing the original destination. Runtime preparation includes the
pinned GNOME Platform and Mesa GL extension; omitting the latter prevents the
sandboxed WebView from rendering.

References: [systemd proc masking investigation](https://github.com/systemd/systemd/issues/34226),
[bubblewrap nested proc report](https://github.com/containers/bubblewrap/issues/707),
[Linux per-user-namespace limits](https://github.com/torvalds/linux/blob/master/kernel/ucount.c).

## Coverage and legacy runners

The container guide records executable coverage and remaining work. Both desktops
now have compositor input/Pinyin, real scale/pointer and live/cold notification
automation. Linux VM helpers are deprecated compatibility tools. New Linux
orchestration belongs in this runner. Hardware, physical
power transitions and different-kernel behavior remain distinct from headless
shared-kernel integration tests; they do not make VMs a permanent prerequisite
for portal testing. Windows VM and real macOS testing are separate workstreams.

The upstream `config.system.build.nspawn` test launcher and NixOS test driver were
used during exploration. They are not the supported host execution path: the
former exposes parent proc/sys for its test infrastructure, while the latter
failed SUID/PAM setup in the Nix build sandbox. The managed runner resolves these
constraints without disabling authentication or application sandboxes.

Standalone Plasma/Xorg is also available as
`nixosConfigurations.runic-headless-kde-xorg`. Select it with the runner's
`--desktop kde --session xorg` options. It uses a private dummy Xorg display and
XTEST input; `--scaling` checks desktop DPI through XSettings. See the
[container automation guide](../../docs/guides/desktop/container-automation.md#gtk-x11-backend)
for coverage and limitations.
