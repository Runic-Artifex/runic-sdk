#!/usr/bin/env bash
# Opens a file and a URL through LaunchServices with an app bundle built around the NativeAOT
# Runic.Platform.Runtime.Tests executable, whose Info.plist registers a URL scheme and plain text. The executable
# notices it runs from a bundle (MacOpenEventsNativeTests.RunBundleProbe), creates its open requests a second after
# it finished launching, which Runic Desktop's application delegate kept the launch's event for, and writes the first
# request's items to <bundle name>.log beside the bundle.
# Before that, the bundle registers itself through IDesktopOpenRegistration (--registration-probe).
# CI runners only: it registers the bundle with LaunchServices, and unregisters it at the end.
# Usage: macos-open-probe.sh <directory of the NativeAOT test executable>
set -euo pipefail
source_directory=$1
work=$(mktemp -d)
# LaunchServices binds URL schemes of applications in an Applications folder; the runner's own is disposable.
applications="$HOME/Applications"
mkdir -p "$applications"
app="$applications/RunicOpenProbe.app"
log="$applications/RunicOpenProbe.log"
lsregister=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
cleanup() {
  "$lsregister" -u "$app" >/dev/null 2>&1 || true
  pkill -f "$app/Contents/MacOS/" >/dev/null 2>&1 || true
  rm -rf "$work" "$app" "$log"
}
trap cleanup EXIT

mkdir -p "$app/Contents/MacOS"
# Only code: codesign refuses other files, such as test results, in Contents/MacOS.
cp "$source_directory/Runic.Platform.Runtime.Tests" "$app/Contents/MacOS/"
find "$source_directory" -maxdepth 1 -name '*.dylib' -exec cp {} "$app/Contents/MacOS/" \;
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
# The registration API first: it checks the Info.plist declarations and registers the bundle with LSRegisterURL
# (MacOpenEventsNativeTests.RunRegistrationProbe). lsregister then registers it again for the launches below.
"$app/Contents/MacOS/Runic.Platform.Runtime.Tests" --registration-probe
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
# The temporary directory may arrive resolved, as /private/var/..., and Uri prints the space unescaped.
grep -qE "^file:///.*/probe( |%20)document\.txt$" "$log" || { echo "The document did not arrive at launch." >&2; exit 1; }

rm -f "$log"
url_opened=""
for _ in 1 2 3 4 5; do
  if open "runic-open-probe://item?id=1"; then url_opened=1; break; fi
  sleep 2
done
if [ -z "$url_opened" ]; then
  echo "LaunchServices did not bind the scheme to the bundle; opening the URL with the bundle explicitly."
  open -a "$app" "runic-open-probe://item?id=1"
fi
wait_for_log
echo "Open URL: $(cat "$log")"
grep -qx "runic-open-probe://item/?id=1" "$log" || { echo "The URL did not arrive at launch." >&2; exit 1; }
echo "PASS LaunchServices delivered a document and a URL to a bundle at launch."
