#!/usr/bin/env bash
# The Window and View journeys (ci.yml `views`). Needs the build job's outputs, Node, npm,
# Bun, Chromium (PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH), Xvfb and network access for the
# example frontends' own installs.
source "$(dirname "${BASH_SOURCE[0]}")/steps.sh"
steps=(first-window desktop-host view-lifetime example-tests typed-clients effect-variant react-vue-variants svelte-angular-outlets development-reload)

step_first_window() {
  node examples/first-window/browser-smoke.mjs
  node examples/first-window/navigation-smoke.mjs
}
step_desktop_host() {
  node examples/first-window-desktop/browser-smoke.mjs
  xvfb-run -a dotnet run --project examples/first-window-desktop/FirstWindowDesktop.csproj -c Release --no-build -- --probe-owner
}
step_view_lifetime() {
  dotnet run --no-build -c Release --project examples/notes-view-first/NotesViewFirst.csproj -- --check-view-lifetime
  dotnet run --no-build -c Release --project examples/notes-view-first/NotesViewFirst.csproj -- --check-view-lifetime --splat
  node examples/notes-view-first/window-smoke.mjs
}
step_example_tests() {
  dotnet run --project examples/notes-view-first/Tests -c Release --no-build
  dotnet run --project examples/multi-window/Tests -c Release --no-build
  (cd examples/notes-view-first/Frontend && bun install --frozen-lockfile && bun run test)
}
step_typed_clients() {
  node examples/notes-view-first/browser-smoke.mjs
  RUNIC_VERIFY_CALL_BURST=1 node examples/notes-reactive-views/browser-smoke.mjs
  RUNIC_VERIFY_PENDING_MOUNT=1 node examples/notes-reactive-views/browser-smoke.mjs
  RUNIC_VERIFY_CLIENT_DISCONNECT=1 node examples/notes-reactive-views/browser-smoke.mjs
}
step_effect_variant() {
  (cd examples/notes-view-first/Effect && bun run --bun build)
  RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/Effect/dist" node examples/notes-view-first/browser-smoke.mjs
}
step_react_vue_variants() {
  (cd examples/notes-view-first/React && bun run --bun build)
  RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/React/dist" node examples/notes-view-first/browser-smoke.mjs
  (cd examples/notes-view-first/Vue && bun run --bun build)
  RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/Vue/dist" node examples/notes-view-first/browser-smoke.mjs
}
step_svelte_angular_outlets() {
  (cd examples/notes-view-first/Svelte && bun install --frozen-lockfile && bun run build)
  RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/Svelte/dist" node examples/notes-view-first/browser-smoke.mjs
  npm --prefix examples/notes-view-first/Angular ci
  npm --prefix examples/notes-view-first/Angular run build
  RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/Angular/dist/angular-composed/browser" node examples/notes-view-first/browser-smoke.mjs
  (cd examples/notes-reactive-views/Svelte && bun install --frozen-lockfile && bun run build)
  RUNIC_WEB_ROOT="$PWD/examples/notes-reactive-views/Svelte/dist" node examples/notes-reactive-views/browser-smoke.mjs
  npm --prefix examples/notes-reactive-views/Angular ci
  npm --prefix examples/notes-reactive-views/Angular run build
  RUNIC_WEB_ROOT="$PWD/examples/notes-reactive-views/Angular/dist/angular-reactive/browser" node examples/notes-reactive-views/browser-smoke.mjs
}
step_development_reload() {
  RUNIC_HMR_FRONTEND=svelte RUNIC_HMR_VIA_RUNIC_DEV=1 node examples/notes-reactive-views/hmr-smoke.mjs
  dotnet build examples/notes-reactive-views/NotesReactiveViews.csproj -c Debug
  node examples/notes-reactive-views/ide-host-smoke.mjs
  node examples/notes-reactive-views/dotnet-hot-reload-smoke.mjs
  RUNIC_HOT_RELOAD_SCENARIO=getter node examples/notes-reactive-views/dotnet-hot-reload-smoke.mjs
}

run_steps "$@"
