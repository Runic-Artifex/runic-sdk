#!/usr/bin/env bash
# Packaging / flatpak (ci.yml `packaging`, target flatpak): build the probe's Flatpak with the
# template's recipe, install it per user, and open it through its URL scheme. Needs .NET, Node,
# Bun, flatpak, flatpak-builder, appstream, desktop-file-utils, the Flathub remote for the
# user, and network access for the GNOME runtime and NuGet.
source "$(dirname "${BASH_SOURCE[0]}")/steps.sh"
PROBE=tests/native/Runic.Packaging.Probe/Runic.Packaging.Probe.csproj
PACKAGE=tools/Runic.Application.Templates/content/runic-app/packaging/package.mjs
steps=(package install open uninstall)

step_package() { node "$PACKAGE" flatpak --project "$PROBE" --output artifacts/package/flatpak; }
step_install() {
  flatpak install --user -y --noninteractive --bundle artifacts/package/flatpak/org.runic.tests.PackagingProbe.flatpak
  local entry=~/.local/share/flatpak/exports/share/applications/org.runic.tests.PackagingProbe.desktop
  desktop-file-validate "$entry"
  grep -qx 'MimeType=x-scheme-handler/runic-packaging-probe;' "$entry"
  test -f ~/.local/share/flatpak/exports/share/icons/hicolor/scalable/apps/org.runic.tests.PackagingProbe.svg
}
step_open() {
  local receipts=${RUNNER_TEMP:-$(mktemp -d)}/receipts
  mkdir -p "$receipts"
  flatpak run --user --filesystem="$receipts" org.runic.tests.PackagingProbe \
    "runic-packaging-probe://receipt?path=$(node -p 'encodeURIComponent(process.argv[1])' "$receipts/packaged.json")"
  bun eng/ci/packaging-probe-check.mjs "$receipts/packaged.json" flatpak
}
step_uninstall() { flatpak uninstall --user -y --noninteractive org.runic.tests.PackagingProbe; }

run_steps "$@"
