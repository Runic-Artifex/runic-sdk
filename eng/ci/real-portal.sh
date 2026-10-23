#!/usr/bin/env bash
# Runs the RealPortalAppearance and RealPortalTrash cases (and RealPortalSecret once) of the portal tests against the real xdg-desktop-portal and its GNOME backend,
# inside the digest-pinned Fedora container that .github/workflows/ci.yml starts. Ubuntu 24.04's portal publishes only
# color-scheme and contrast; Fedora's (1.22 with xdg-desktop-portal-gnome 50) also publishes accent-color and
# reduced-motion. The container needs no .NET: the job passes the published test executables (self-contained JIT and
# NativeAOT). It installs as root, then runs the tests as the checkout owner on a private session bus. Locally:
#
#   podman run --rm --init --userns=keep-id --user root -v "$PWD:$PWD" -w "$PWD" \
#     <FEDORA_IMAGE from ci.yml> bash eng/ci/real-portal.sh artifacts/portal-jit/Runic.Platform.Linux.Portal.Tests
#
# Add -v <directory>:/var/cache/libdnf5 to reuse downloaded packages (eng/ci/README.md, "Fedora packages").
#
# Usage: real-portal.sh <test executable>...
set -euo pipefail
dir=$(cd "$(dirname "$0")" && pwd)

# Downloads the packages with a time limit per attempt, then installs them without network access. A mirror slower
# than minrate for 30 seconds is dropped for the metalink's next mirror; dnf's default (1000 B/s) let a throttled
# mirror hold the download until the step's limit. Signatures stay checked (gpgcheck), also for cached packages.
install_packages() {
  local cache=/var/cache/libdnf5 log limit used attempt failures=() downloaded=false
  local packages=(xdg-desktop-portal xdg-desktop-portal-gnome gnome-keyring dconf gsettings-desktop-schemas glib2 dbus-daemon libicu util-linux)
  local dnf=(dnf -y --disablerepo=fedora-cisco-openh264 --setopt=install_weak_deps=False --setopt=keepcache=True
    --setopt=timeout=30 --setopt=minrate=100k)
  log=$(mktemp)
  echo "Package cache: $(find "$cache" -name '*.rpm' 2>/dev/null | wc -l) packages"
  # A healthy mirror delivers the metadata and the 145 MB of packages in under a minute. Each attempt refreshes the
  # metadata; packages completed by a stopped attempt are kept.
  for limit in 120 150; do
    used=$SECONDS attempt=$((${#failures[@]} + 1))
    if "$dir/retry.sh" 1 "$limit" "${dnf[@]}" install --downloadonly "${packages[@]}" 2>&1 | tee "$log"; then
      downloaded=true
      echo "Downloaded in $((SECONDS - used))s"
      break
    fi
    failures+=("attempt $attempt via the mirrors ($((SECONDS - used))s): $(summary "$log")")
  done
  # The restored cache alone, whatever its age, when the mirrors are unusable.
  if [ "$downloaded" != true ] && "${dnf[@]}" --cacheonly install --downloadonly "${packages[@]}" >"$log" 2>&1; then
    downloaded=true
    echo "::warning title=Fedora mirrors unusable::Installing the portal packages from the package cache: ${failures[*]}"
  fi
  if [ "$downloaded" != true ]; then
    failures+=("package cache: $(summary "$log")")
    echo "::error title=Fedora packages could not be downloaded::${packages[*]} could not be downloaded. $(printf '%s. ' "${failures[@]}")"
    exit 1
  fi
  "$dir/retry.sh" 1 300 "${dnf[@]}" --cacheonly install "${packages[@]}"
  rpm -q xdg-desktop-portal xdg-desktop-portal-gnome gnome-keyring gsettings-desktop-schemas
  # The cache keeps the installed versions only, so superseded packages do not accumulate in it, and not dnf's
  # solv files, which it regenerates from the metadata.
  local installed
  installed=$(rpm -qa --qf '%{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}.rpm\n')
  find "$cache" -name '*.rpm' 2>/dev/null | while read -r file; do
    grep -qxF "$(basename "$file")" <<<"$installed" || rm -f "$file"
  done
  rm -rf "$cache"/*/solv
  rm -f "$log"
}

# What a failed attempt was doing: dnf's errors, or how far the download got when the time limit stopped it.
summary() {
  local errors last
  errors=$(grep -E '^(Error|error|>>> )|Curl error|Cannot download|No more mirrors' "$1" | awk '!seen[$0]++' | head -n 3 | paste -sd';' - || true)
  if grep -q '::warning::Timed out' "$1"; then
    last=$(grep -E '^ *(\[ *[0-9]+/[0-9]+\]|Fedora)' "$1" | tail -n 1 | tr -s ' ' || true)
    echo "timed out after $(grep -cE '^\[ *[0-9]+/[0-9]+\] ' "$1") of the package downloads, last completed: ${last:-none, still loading the repository metadata}${errors:+; $errors}"
  else
    echo "${errors:-dnf failed without an error line}"
  fi
}

if [ "${1:-}" != --checks ]; then
  test $# -gt 0 || { echo "usage: real-portal.sh <test executable>..." >&2; exit 2; }
  owner=$(stat -c %u .)
  if [ "$owner" = 0 ]; then echo "real-portal.sh: the checkout must not be owned by root in the container; see the usage above." >&2; exit 2; fi
  echo "::group::Install the portal and its GNOME backend"
  grep PRETTY_NAME /etc/os-release
  install_packages
  # Podman's keep-id adds a passwd entry for the uid; a docker run does not.
  user=$(awk -F: -v uid="$owner" '$3 == uid { print $1; exit }' /etc/passwd)
  if [ -z "$user" ]; then user=runic; useradd -M -u "$owner" "$user"; fi
  echo "::endgroup::"
  exec runuser -u "$user" -- bash "$0" --checks "$@"
fi
shift
export HOME XDG_RUNTIME_DIR XDG_CURRENT_DESKTOP=GNOME RUNIC_TEST_REAL_PORTAL=gnome
HOME=$(mktemp -d); XDG_RUNTIME_DIR=$(mktemp -d)
mkdir -p "$HOME/.config/xdg-desktop-portal"
# GNOME Keyring implements the Secret portal's backend; the GNOME backend does not.
printf '[preferred]\ndefault=gnome\norg.freedesktop.impl.portal.Secret=gnome-keyring\n' > "$HOME/.config/xdg-desktop-portal/portals.conf"
for tests in "$@"; do
  # Each run takes about a second; the limit turns a stuck portal activation into a failure with the test's output.
  echo "::group::RealPortalAppearance with $tests"
  # The session bus activates xdg-desktop-portal and its backend on the first Settings call.
  dbus-run-session -- "$tests" --treenode-filter "/*/*/*/*[Category=RealPortalAppearance]" --timeout 2m --output detailed
  echo "::endgroup::"
  echo "::group::RealPortalTrash with $tests"
  # The portal's own Trash implementation moves the items into this temporary home's trash.
  dbus-run-session -- "$tests" --treenode-filter "/*/*/*/*[Category=RealPortalTrash]" --timeout 2m --output detailed
  echo "::endgroup::"
done
# The Secret portal once, with the first executable (the JIT build). An unlocked login keyring whose daemon serves
# org.freedesktop.impl.portal.Secret; the portal asks it for the application's secret, which keys the libsecret file
# in a temporary directory. The portal offers the Secret interface only if its backend (org.freedesktop.secrets,
# gnome-keyring.portal) exports it when the portal starts, so the case waits for the exported interface first. In a
# second session in the same container the portal still intermittently started without its Secret interface
# (UnknownMethod); the NativeAOT build covers the same client against the fake portal (Category=DBus).
# shellcheck disable=SC2016 # $0 expands in the inner shell.
XDG_RUNTIME_DIR=$(mktemp -d) dbus-run-session -- sh -c 'printf runic | gnome-keyring-daemon --unlock --components=secrets >/dev/null &&
  for _ in $(seq 100); do
    gdbus introspect --session --dest org.freedesktop.secrets --object-path /org/freedesktop/portal/desktop 2>/dev/null |
      grep -q "interface org.freedesktop.impl.portal.Secret" && break
    sleep 0.2
  done &&
  exec "$0" --treenode-filter "/*/*/*/*[Category=RealPortalSecret]" --timeout 2m --output detailed' "$1"
echo "::endgroup::"
