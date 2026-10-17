#!/usr/bin/env bash
# Opens a file and a URL through LaunchServices with an app bundle built around the NativeAOT
# Runic.Platform.Runtime.Tests executable, whose Info.plist registers a URL scheme and plain text. The executable
# notices it runs from a bundle (MacOpenEventsNativeTests.RunBundleProbe), creates its open requests before
# finishing launching, and writes the first request's items to <bundle name>.log beside the bundle.
# CI runners only: it registers the bundle with LaunchServices, and unregisters it at the end.
# Usage: macos-open-probe.sh <directory of the NativeAOT test executable>
set -euo pipefail
source_directory=$1
work=$(mktemp -d)
app="$work/RunicOpenProbe.app"
log="$work/RunicOpenProbe.log"
lsregister=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
cleanup() {
  "$lsregister" -u "$app" >/dev/null 2>&1 || true
  pkill -f "$app/Contents/MacOS/" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

mkdir -p "$app/Contents/MacOS"
cp -R "$source_directory"/. "$app/Contents/MacOS/"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>org.runic.tests.OpenProbe</string>
  <key>CFBundleName</key><string>RunicOpenProbe</string>
  <key>CFBundleExecutable</key><string>Runic.Platform.Runtime.Tests</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>LSUIElement</key><true/>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>org.runic.tests.OpenProbe</string>
      <key>CFBundleURLSchemes</key><array><string>runic-open-probe</string></array>
    </dict>
  </array>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Plain text</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key><array><string>public.plain-text</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST
codesign --force --sign - "$app"
"$lsregister" -f "$app"

wait_for_log() {
  for _ in $(seq 1 60); do
    if [ -s "$log" ] && ! pgrep -f "$app/Contents/MacOS/" >/dev/null; then return 0; fi
    sleep 1
  done
  echo "No open request was recorded within 60 seconds." >&2
  return 1
}

document="$work/probe document.txt"
echo probe > "$document"
open -a "$app" "$document"
wait_for_log
echo "Open document: $(cat "$log")"
# The temporary directory may arrive resolved, as /private/var/...; the file name is what matters.
grep -q "^file:///.*/probe%20document\.txt$" "$log" || { echo "The document did not arrive at launch." >&2; exit 1; }

rm -f "$log"
open "runic-open-probe://item?id=1"
wait_for_log
echo "Open URL: $(cat "$log")"
grep -qx "runic-open-probe://item/?id=1" "$log" || { echo "The URL did not arrive at launch." >&2; exit 1; }
echo "PASS LaunchServices delivered a document and a URL to a bundle at launch."
