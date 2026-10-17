#!/bin/sh
# The Native job's checks for a musl RID, inside the digest-pinned Alpine container
# that .github/workflows/ci.yml starts with `docker run` (GitHub cannot run
# JavaScript actions in an Alpine container on arm64 runners, so the host checks out
# and caches and this script does the rest). It installs the toolchain as root, then
# runs the checks as a user with the checkout owner's uid, as a desktop application
# runs. Locally, with rootless Podman (keep-id maps your uid into the container):
#
#   podman run --rm --init --userns=keep-id --user root --security-opt seccomp=unconfined \
#     --shm-size 1g -v "$PWD:$PWD" -w "$PWD" -e NUGET_PACKAGES="$PWD/.cache/nuget" \
#     <alpine image from ci.yml> sh eng/ci/native-musl.sh linux-musl-x64
#
# Runic Desktop bundles no Linux native library: it loads the distribution's GTK 3
# and WebKitGTK 4.1, so the checks use Alpine's packages. WebKit's bubblewrap
# sandbox stays enabled; the container runs without the default seccomp filter so
# bwrap can create its namespaces, and the script proves that before any window.
# Usage: native-musl.sh <linux-musl-x64|linux-musl-arm64>
set -eu
rid=${1:?usage: native-musl.sh <rid>}
case "$rid" in
  linux-musl-x64 | linux-musl-arm64) ;;
  *) echo "native-musl.sh: unknown RID $rid" >&2; exit 2 ;;
esac
step() { echo "::endgroup::"; echo "::group::$*"; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 CI=true GDK_BACKEND=x11

if [ "${2:-}" != --checks ]; then
echo "::group::Install Alpine packages"
grep PRETTY_NAME /etc/os-release
uname -m
retry() { n=0; until "$@"; do n=$((n + 1)); [ "$n" -lt 3 ] || return 1; sleep 10; done; }
retry apk add --no-cache \
  bash curl git jq icu-libs krb5-libs libgcc libstdc++ zlib \
  clang build-base zlib-dev \
  gtk+3.0 webkit2gtk-4.1 bubblewrap xvfb-run dbus dbus-x11 at-spi2-core mesa-dri-gallium mesa-gles font-dejavu su-exec

step "Install .NET"
# The SDK pinned in global.json, verified against Microsoft's release metadata.
sdk=$(jq -r .sdk.version global.json)
file=$(curl -fsSL "https://builds.dotnet.microsoft.com/dotnet/release-metadata/${sdk%%.*}.0/releases.json" \
  | jq -c --arg sdk "$sdk" --arg rid "$rid" '[.releases[].sdks[]? | select(.version == $sdk) | .files[]
      | select(.rid == $rid and (.name | endswith(".tar.gz")))][0]')
test "$file" != null
work=$(mktemp -d)
retry curl -fsSL -o "$work/dotnet.tar.gz" "$(echo "$file" | jq -r .url)"
echo "$(echo "$file" | jq -r .hash)  $work/dotnet.tar.gz" | sha512sum -c -
mkdir -p /usr/share/dotnet
tar -xzf "$work/dotnet.tar.gz" -C /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
rm -rf "$work"
dotnet --info | head -20

owner=$(stat -c %u .)
if [ "$owner" = 0 ]; then echo "native-musl.sh: the checkout must not be owned by root in the container; see the usage above." >&2; exit 2; fi
# Podman's keep-id adds a passwd entry for the uid; a docker run does not.
user=$(awk -F: -v uid="$owner" '$3 == uid { print $1; exit }' /etc/passwd)
if [ -z "$user" ]; then user=runic; adduser -D -H -u "$owner" "$user"; fi
home=$(mktemp -d)
chown "$owner" "$home"
echo "::endgroup::"
exec su-exec "$user" env HOME="$home" DOTNET_CLI_HOME="$home" sh "$0" "$rid" --checks
fi

echo "::group::Prove the WebKit sandbox can start as $(id -un)"
bwrap --unshare-user --unshare-net --ro-bind / / --proc /proc --dev /dev true

smoke=tests/native/Runic.Desktop.WebViewSmoke/bin/Release/net10.0/Runic.Desktop.WebViewSmoke.dll
aot="-c Release -r $rid --self-contained true -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true"

step "Exercise the GTK 3 native window and event loop with JIT"
dotnet build tests/native/Runic.Desktop.WebViewSmoke -c Release
xvfb-run -a dotnet "$smoke"
xvfb-run -a dotnet "$smoke" --event-loop
# The glibc Linux jobs run the keyboard driver. Its zoom checks read devicePixelRatio, which
# includes the page zoom in Ubuntu's WebKitGTK 2.52 but not in Alpine's 2.48.

step "Exercise the GTK 3 native window with NativeAOT"
# shellcheck disable=SC2086
dotnet publish tests/native/Runic.Desktop.WebViewSmoke $aot -p:TrimmerSingleWarn=false -o artifacts/webview-aot
xvfb-run -a artifacts/webview-aot/Runic.Desktop.WebViewSmoke
xvfb-run -a artifacts/webview-aot/Runic.Desktop.WebViewSmoke --event-loop

step "Verify the native GTK clipboard and directory chooser with JIT"
dotnet build tests/dotnet/Runic.Platform.Linux.Tests -c Release -p:TreatWarningsAsErrors=true
linux_tests=tests/dotnet/Runic.Platform.Linux.Tests/bin/Release/net10.0/Runic.Platform.Linux.Tests.dll
xvfb-run -a dotnet "$linux_tests" --treenode-filter "/*/*/*/*[Category=Native]"
GSETTINGS_BACKEND=memory xvfb-run -a dotnet "$linux_tests" --treenode-filter "/*/*/*/*[Category=DirectoryNative]"
GSETTINGS_BACKEND=memory xvfb-run -a dotnet "$linux_tests" --treenode-filter "/*/*/*/*[Category=DirectoryLoopStopped]"

step "Verify the GTK clipboard with NativeAOT"
# shellcheck disable=SC2086
dotnet publish tests/dotnet/Runic.Platform.Linux.Tests $aot -p:TrimmerSingleWarn=false -o artifacts/clipboard-aot
artifacts/clipboard-aot/Runic.Platform.Linux.Tests
xvfb-run -a artifacts/clipboard-aot/Runic.Platform.Linux.Tests --treenode-filter "/*/*/*/*[Category=Native]"

step "Exercise the direct portal protocol with JIT and NativeAOT"
dbus-run-session -- dotnet run --project tests/dotnet/Runic.Platform.Linux.Portal.Tests -c Release -- --treenode-filter "/*/*/*/*[Category=DBus]"
# shellcheck disable=SC2086
dotnet publish tests/dotnet/Runic.Platform.Linux.Portal.Tests $aot -o artifacts/portal-aot
dbus-run-session -- artifacts/portal-aot/Runic.Platform.Linux.Portal.Tests
dbus-run-session -- artifacts/portal-aot/Runic.Platform.Linux.Portal.Tests --treenode-filter "/*/*/*/*[Category=DBus]"

step "Verify privileged actions and platform runtime conformance with NativeAOT"
# shellcheck disable=SC2086
dotnet publish tests/dotnet/Runic.Platform.Linux.Polkit.Tests $aot -o artifacts/polkit-aot
artifacts/polkit-aot/Runic.Platform.Linux.Polkit.Tests
# shellcheck disable=SC2086
dotnet publish tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj $aot -o artifacts/platform-runtime-aot
artifacts/platform-runtime-aot/Runic.Platform.Runtime.Tests
echo "::endgroup::"
echo "Native checks passed on $rid."
