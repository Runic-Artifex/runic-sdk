#!/bin/sh
# Runs the RealPortalAppearance case of the portal tests against the real xdg-desktop-portal and its GNOME backend,
# inside the digest-pinned Fedora container that .github/workflows/ci.yml starts. Ubuntu 24.04's portal publishes only
# color-scheme and contrast; Fedora's (1.22 with xdg-desktop-portal-gnome 50) also publishes accent-color and
# reduced-motion. The container needs no .NET: the job passes the published test executables (self-contained JIT and
# NativeAOT). It installs as root, then runs the tests as the checkout owner on a private session bus. Locally:
#
#   podman run --rm --init --userns=keep-id --user root -v "$PWD:$PWD" -w "$PWD" \
#     <FEDORA_IMAGE from ci.yml> sh eng/ci/real-portal.sh artifacts/portal-jit/Runic.Platform.Linux.Portal.Tests
#
# Usage: real-portal.sh <test executable>...
set -eu
if [ "${1:-}" != --checks ]; then
  test $# -gt 0 || { echo "usage: real-portal.sh <test executable>..." >&2; exit 2; }
  echo "::group::Install the portal and its GNOME backend"
  grep PRETTY_NAME /etc/os-release
  n=0; until dnf -q -y --setopt=install_weak_deps=False install xdg-desktop-portal xdg-desktop-portal-gnome dconf gsettings-desktop-schemas glib2 dbus-daemon libicu util-linux; do
    n=$((n + 1)); [ "$n" -lt 3 ] || exit 1; sleep 10; done
  rpm -q xdg-desktop-portal xdg-desktop-portal-gnome gsettings-desktop-schemas
  owner=$(stat -c %u .)
  if [ "$owner" = 0 ]; then echo "real-portal.sh: the checkout must not be owned by root in the container; see the usage above." >&2; exit 2; fi
  # Podman's keep-id adds a passwd entry for the uid; a docker run does not.
  user=$(awk -F: -v uid="$owner" '$3 == uid { print $1; exit }' /etc/passwd)
  if [ -z "$user" ]; then user=runic; useradd -M -u "$owner" "$user"; fi
  echo "::endgroup::"
  exec runuser -u "$user" -- sh "$0" --checks "$@"
fi
shift
export HOME XDG_RUNTIME_DIR XDG_CURRENT_DESKTOP=GNOME RUNIC_TEST_REAL_PORTAL=gnome
HOME=$(mktemp -d); XDG_RUNTIME_DIR=$(mktemp -d)
mkdir -p "$HOME/.config/xdg-desktop-portal"
printf '[preferred]\ndefault=gnome\n' > "$HOME/.config/xdg-desktop-portal/portals.conf"
for tests in "$@"; do
  echo "::group::RealPortalAppearance with $tests"
  # The session bus activates xdg-desktop-portal and its backend on the first Settings call.
  dbus-run-session -- "$tests" --treenode-filter "/*/*/*/*[Category=RealPortalAppearance]" --output detailed
  echo "::endgroup::"
done
