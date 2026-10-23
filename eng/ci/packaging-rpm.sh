#!/usr/bin/env bash
# Installs the probe's rpm from dotnet runic package rpm (built on Ubuntu) in the digest-pinned Fedora container that
# .github/workflows/ci.yml starts, and opens it with its URL scheme. dnf resolves the package's only requirement,
# WebKitGTK (AutoReqProv is off, because the NativeAOT binary loads GTK with dlopen), from Fedora's repositories. The
# download is time-limited and cached as in eng/ci/real-portal.sh (eng/ci/README.md, "Fedora packages"). Locally:
#
#   podman run --rm --init -v "$PWD:$PWD" -w "$PWD" <FEDORA_IMAGE from ci.yml> \
#     bash eng/ci/packaging-rpm.sh artifacts/package/rpm/runic.packaging.probe-1.2.3-1.x86_64.rpm \
#     "runic-packaging-probe://receipt?path=$(node -p 'encodeURIComponent(process.argv[1])' "$PWD/receipt.json")"
#
# Usage: packaging-rpm.sh <rpm> <URL to open>
set -euo pipefail
dir=$(cd "$(dirname "$0")" && pwd)
test $# -eq 2 || { echo "usage: packaging-rpm.sh <rpm> <URL to open>" >&2; exit 2; }
rpm=$(realpath "$1") url=$2
grep PRETTY_NAME /etc/os-release

cache=/var/cache/libdnf5
dnf=(dnf -y --disablerepo=fedora-cisco-openh264 --setopt=install_weak_deps=False --setopt=keepcache=True
  --setopt=timeout=30 --setopt=minrate=100k)
echo "::group::Install the rpm and WebKitGTK"
echo "Package cache: $(find "$cache" -name '*.rpm' 2>/dev/null | wc -l) packages"
downloaded=false
for limit in 150 180; do
  if "$dir/retry.sh" 1 "$limit" "${dnf[@]}" install --downloadonly "$rpm"; then downloaded=true; break; fi
done
if [ "$downloaded" != true ] && "${dnf[@]}" --cacheonly install --downloadonly "$rpm"; then
  downloaded=true
  echo "::warning title=Fedora mirrors unusable::Installing WebKitGTK from the package cache"
fi
if [ "$downloaded" != true ]; then
  echo "::error title=Fedora packages could not be downloaded::The rpm's dependencies could not be downloaded."
  exit 1
fi
"$dir/retry.sh" 1 300 "${dnf[@]}" --cacheonly install "$rpm"
# The cache keeps the installed versions only, and not dnf's solv files.
installed=$(rpm -qa --qf '%{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}.rpm\n')
find "$cache" -name '*.rpm' 2>/dev/null | while read -r file; do
  grep -qxF "$(basename "$file")" <<<"$installed" || rm -f "$file"
done
rm -rf "$cache"/*/solv
echo "::endgroup::"

rpm -q webkit2gtk4.1 gtk3
name=$(rpm -qp --qf '%{NAME}' "$rpm")
# The application, its link on PATH and its desktop integration.
rpm -ql "$name" | grep -qx /usr/lib/org.runic.tests.PackagingProbe/Runic.Packaging.Probe
test -L /usr/bin/Runic.Packaging.Probe
entry=/usr/share/applications/org.runic.tests.PackagingProbe.desktop
grep -qx 'MimeType=x-scheme-handler/runic-packaging-probe;application/x-packagingprobe-runicprobe;' "$entry"
grep -qx 'Exec=/usr/bin/Runic.Packaging.Probe' /usr/share/dbus-1/services/org.runic.tests.PackagingProbe.service
grep -q 'glob pattern="\*.runicprobe"' /usr/share/mime/packages/org.runic.tests.PackagingProbe.xml
# Opened through its URL scheme as the desktop does: the entry's Exec with the URL for %U.
Runic.Packaging.Probe "$url"
