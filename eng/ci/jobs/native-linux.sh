#!/usr/bin/env bash
# The Linux checks of the native job (ci.yml `native`, linux-x64 and linux-arm64 legs):
# desktop contracts, GTK 3/4 windows under Xvfb, private-bus D-Bus and portal protocol,
# GNOME Keyring, NativeAOT consumers, AT-SPI screen-reader checks, clipboard, directory
# chooser and open requests. RID defaults to linux-x64.
#
# Needs .NET, Bun, Node, Chromium (PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH), GTK 3/4 with
# WebKitGTK 4.1/6.0, Xvfb (xvfb-run), dbus-run-session, AT-SPI, xdotool, GNOME Keyring,
# secret-tool, gio, the ar_EG.UTF-8 locale and network access for RID-specific restores.
# The real-portal check runs separately in Fedora (eng/ci/real-portal.sh).
source "$(dirname "${BASH_SOURCE[0]}")/steps.sh"
RID=${RID:-linux-x64}
steps=(views-runtime desktop-contracts webview-smoke webview-event-loop gtk3-keyboard keyboard-script portal-protocol dbus-api
  gtk4-services nativeaot-consumers secret-service polkit-aot accessibility clipboard directory-chooser clipboard-aot
  platform-runtime-aot open-registration views-aot)
gtk() { GDK_BACKEND=x11 GSETTINGS_BACKEND=memory dbus-run-session -- xvfb-run -a "$@"; }
aot=(-c Release -r "$RID" --self-contained true -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true)
linux_tests=tests/dotnet/Runic.Platform.Linux.Tests/bin/Release/net10.0/Runic.Platform.Linux.Tests.dll

# Generated Views clients import the workspace runtime from its build output.
step_views_runtime() { (cd packages/web/views && bun run --bun build); }
# --timeout bounds the whole session, including test-host startup and exit.
step_desktop_contracts() {
  dotnet build tests/dotnet/Runic.Desktop.Tests -c Release
  timeout 5m dotnet run --project tests/dotnet/Runic.Desktop.Tests -c Release --no-build -- \
    --timeout 3m --output detailed --diagnostic --diagnostic-output-directory "$PWD/artifacts/native-tests" \
    --results-directory "$PWD/artifacts/native-tests" --report-trx --report-trx-filename desktop.trx
}
# Real pointer drags through XTEST: a stuck drag must not hold the job.
step_webview_smoke() {
  dotnet build tests/native/Runic.Desktop.WebViewSmoke -c Release
  GDK_BACKEND=x11 timeout 6m xvfb-run -a dotnet tests/native/Runic.Desktop.WebViewSmoke/bin/Release/net10.0/Runic.Desktop.WebViewSmoke.dll
}
step_webview_event_loop() { GDK_BACKEND=x11 xvfb-run -a dotnet tests/native/Runic.Desktop.WebViewSmoke/bin/Release/net10.0/Runic.Desktop.WebViewSmoke.dll --event-loop; }
# Real key presses and wheel clicks through XTEST; see keyboard-driver.sh.
step_gtk3_keyboard() {
  GDK_BACKEND=x11 timeout 8m xvfb-run -a tests/native/Runic.Desktop.WebViewSmoke/keyboard-driver.sh artifacts/keyboard/gtk3 dotnet tests/native/Runic.Desktop.WebViewSmoke/bin/Release/net10.0/Runic.Desktop.WebViewSmoke.dll
}
step_keyboard_script() { timeout 5m bun test --timeout 60000 tests/native/Runic.Desktop.WebViewSmoke/keyboard-script.test.mjs; }
# The explicit DBus cases need a session bus; the managed job runs the portable cases.
step_portal_protocol() {
  dbus-run-session -- dotnet run --project tests/dotnet/Runic.Platform.Linux.Portal.Tests -c Release -- --treenode-filter "/*/*/*/*[Category=DBus]"
}
step_dbus_api() {
  dbus-run-session -- dotnet run --project tests/dotnet/Runic.Platform.Linux.DBus.Tests -c Release -- --treenode-filter "/*/*/*/*[Category=DBus]"
}
step_gtk4_services() {
  local project=(--project tests/native/Runic.Desktop.Gtk4.Smoke -c Release)
  gtk dotnet run "${project[@]}"
  # The GTK runner has one lifetime per process; the entry-point check runs separately.
  gtk dotnet run "${project[@]}" --no-build -- --event-loop
  # SIGTERM closes the GTK window and disposes the host before the GTK loop ends.
  gtk dotnet run "${project[@]}" --no-build -- --signal
  # DesktopWindow.AnnounceAsync reaches AT-SPI as Object:Announcement with GTK 3 and GTK 4 (#318).
  gtk dotnet run "${project[@]}" --no-build -- --announcement gtk3
  gtk dotnet run "${project[@]}" --no-build -- --announcement gtk4
  # Right-to-left (W315-019): an Arabic process locale reaches the page's navigator.language, GTK 4's own
  # direction is right to left without a translation, and the application can override it.
  LC_ALL=ar_EG.UTF-8 LANG=ar_EG.UTF-8 LANGUAGE=ar gtk dotnet run "${project[@]}" --no-build -- --rtl
  # Controlled-portal journey: directory and SaveFile requests through the production provider.
  # A private runtime directory lets the isolated bus activate its own document portal, when
  # installed, for the document-view save; otherwise that variant reports a skip with its reason.
  local journey_runtime
  journey_runtime=$(mktemp -d) && mkdir "$journey_runtime/data"
  XDG_RUNTIME_DIR="$journey_runtime" XDG_DATA_HOME="$journey_runtime/data" gtk dotnet run "${project[@]}" --no-build -- --directory
  # Without a display the entry point must fall back instead of letting gtk_init exit the process.
  env -u DISPLAY -u WAYLAND_DISPLAY GDK_BACKEND=x11 GSETTINGS_BACKEND=memory dotnet run "${project[@]}" --no-build -- --no-display
}
step_nativeaot_consumers() {
  dotnet publish tests/native/Runic.Desktop.Gtk4.Smoke "${aot[@]}" -o artifacts/gtk4-aot
  gtk artifacts/gtk4-aot/Runic.Desktop.Gtk4.Smoke
  gtk artifacts/gtk4-aot/Runic.Desktop.Gtk4.Smoke --event-loop
  gtk tests/native/Runic.Desktop.WebViewSmoke/keyboard-driver.sh artifacts/keyboard/gtk4-aot artifacts/gtk4-aot/Runic.Desktop.Gtk4.Smoke
  env -u DISPLAY -u WAYLAND_DISPLAY GDK_BACKEND=x11 GSETTINGS_BACKEND=memory artifacts/gtk4-aot/Runic.Desktop.Gtk4.Smoke --no-display
  dotnet publish tests/dotnet/Runic.Platform.Linux.Portal.Tests "${aot[@]}" -o artifacts/portal-aot
  # The run without a filter keeps the portable cases covered under NativeAOT.
  dbus-run-session -- artifacts/portal-aot/Runic.Platform.Linux.Portal.Tests
  dbus-run-session -- artifacts/portal-aot/Runic.Platform.Linux.Portal.Tests --treenode-filter "/*/*/*/*[Category=DBus]"
  dotnet publish tests/dotnet/Runic.Platform.Linux.DBus.Tests "${aot[@]}" -o artifacts/dbus-aot
  dbus-run-session -- artifacts/dbus-aot/Runic.Platform.Linux.DBus.Tests
  dbus-run-session -- artifacts/dbus-aot/Runic.Platform.Linux.DBus.Tests --treenode-filter "/*/*/*/*[Category=DBus]"
}
# A real Secret Service: GNOME Keyring with an unlocked login keyring on a private session bus, from the JIT build
# and the NativeAOT executable. The case requires the encrypted session and has secret-tool read what it stored.
step_secret_service() {
  local tests
  for tests in "dotnet tests/dotnet/Runic.Platform.Linux.Portal.Tests/bin/Release/net10.0/Runic.Platform.Linux.Portal.Tests.dll" artifacts/portal-aot/Runic.Platform.Linux.Portal.Tests; do
    RUNIC_TEST_SECRET_SERVICE=gnome-keyring timeout 8m dbus-run-session -- sh -c "printf runic | gnome-keyring-daemon --unlock --components=secrets >/dev/null && $tests --treenode-filter '/*/*/*/*[Category=SecretService]' --output detailed"
  done
}
# The managed job runs the same cases under JIT; nixos/privileged-operations covers a real Polkit.
step_polkit_aot() {
  dotnet publish tests/dotnet/Runic.Platform.Linux.Polkit.Tests "${aot[@]}" -o artifacts/polkit-aot
  artifacts/polkit-aot/Runic.Platform.Linux.Polkit.Tests
}
# W315-018: Runic.Desktop.AccessibilityProbe reads and changes a page through AT-SPI (GTK 3 and GTK 4) from its own
# process, as a screen reader does. Expectations.cs lists each host's known gaps.
step_accessibility() {
  mkdir -p artifacts/accessibility
  dotnet build tests/native/Runic.Desktop.AccessibilityProbe -c Release -f net10.0
  local probe=(dotnet "$PWD/tests/native/Runic.Desktop.AccessibilityProbe/bin/Release/net10.0/Runic.Desktop.AccessibilityProbe.dll") toolkit
  for toolkit in gtk3 gtk4; do
    timeout 12m env GDK_BACKEND=x11 GSETTINGS_BACKEND=memory dbus-run-session -- xvfb-run -a dotnet run --project tests/native/Runic.Desktop.Gtk4.Smoke \
      -c Release --no-build -- --accessibility "$toolkit" --probe "${probe[@]}" --receipt "artifacts/accessibility/$toolkit.json"
  done
}
step_clipboard() {
  dotnet build tests/dotnet/Runic.Platform.Linux.Tests -c Release -p:TreatWarningsAsErrors=true
  GDK_BACKEND=x11 timeout 3m xvfb-run -a dotnet "$linux_tests" --treenode-filter "/*/*/*/*[Category=Native]"
}
step_directory_chooser() {
  GDK_BACKEND=x11 GSETTINGS_BACKEND=memory timeout 3m xvfb-run -a dotnet "$linux_tests" --treenode-filter "/*/*/*/*[Category=DirectoryNative]"
  GDK_BACKEND=x11 GSETTINGS_BACKEND=memory timeout 3m xvfb-run -a dotnet "$linux_tests" --treenode-filter "/*/*/*/*[Category=DirectoryLoopStopped]"
}
step_clipboard_aot() {
  timeout 10m dotnet publish tests/dotnet/Runic.Platform.Linux.Tests "${aot[@]}" -p:TrimmerSingleWarn=false -o artifacts/clipboard-aot
  artifacts/clipboard-aot/Runic.Platform.Linux.Tests
  GDK_BACKEND=x11 timeout 3m xvfb-run -a artifacts/clipboard-aot/Runic.Platform.Linux.Tests --treenode-filter "/*/*/*/*[Category=Native]"
}
step_platform_runtime_aot() {
  timeout 10m dotnet publish tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj "${aot[@]}" -o artifacts/platform-runtime-aot
  ./artifacts/platform-runtime-aot/Runic.Platform.Runtime.Tests
}
# A desktop entry opened with gio in an isolated session without a display.
step_open_registration() {
  dotnet build tests/dotnet/Runic.Platform.Runtime.Tests -c Release
  local tests session
  for tests in tests/dotnet/Runic.Platform.Runtime.Tests/bin/Release/net10.0/Runic.Platform.Runtime.Tests artifacts/platform-runtime-aot/Runic.Platform.Runtime.Tests; do
    session=$(mktemp -d)
    mkdir -p "$session/home" "$session/data" "$session/config" "$session/system-data" "$session/system-config" "$session/run"
    chmod 700 "$session/run"
    RUNIC_TEST_OPEN_REGISTRATION=1 timeout 8m env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS HOME="$session/home" XDG_DATA_HOME="$session/data" \
      XDG_CONFIG_HOME="$session/config" XDG_DATA_DIRS="$session/system-data" XDG_CONFIG_DIRS="$session/system-config" \
      XDG_RUNTIME_DIR="$session/run" dbus-run-session -- "$tests" --treenode-filter "/*/*/*/*[Category=NativeOpen]" --output detailed
    rm -rf "$session"
  done
}
# Only for RIDs where CS-WebUI ships a native library (eng/support.json; nativeLegs cswebui).
step_views_aot() {
  dotnet publish examples/first-window/FirstWindow.csproj "${aot[@]}" -o artifacts/views-aot
  dotnet publish tests/fixtures/application/cswebui-window-probes/CsWebUiWindowProbes.csproj "${aot[@]}" -o artifacts/views-probes-aot
  GDK_BACKEND=x11 xvfb-run -a ./artifacts/views-probes-aot/CsWebUiWindowProbes --probe-factory-failure
  GDK_BACKEND=x11 xvfb-run -a ./artifacts/views-probes-aot/CsWebUiWindowProbes --probe-window-close
  GDK_BACKEND=x11 xvfb-run -a ./artifacts/views-probes-aot/CsWebUiWindowProbes --probe-close-guard
  GDK_BACKEND=x11 xvfb-run -a ./artifacts/views-probes-aot/CsWebUiWindowProbes --probe-missing-bridge
  GDK_BACKEND=x11 xvfb-run -a ./artifacts/views-probes-aot/CsWebUiWindowProbes --probe-show-failure
  GDK_BACKEND=x11 RUNIC_FIRST_WINDOW_EXECUTABLE="$PWD/artifacts/views-aot/FirstWindow" xvfb-run -a node examples/first-window/browser-smoke.mjs
}

run_steps "$@"
