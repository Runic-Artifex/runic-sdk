#!/usr/bin/env bash
# The packed-package consumers (ci.yml `package-consumers`). Needs the package candidates in
# artifacts/packages, Node, npm, Bun, Chromium and network access to nuget.org and npm.
source "$(dirname "${BASH_SOURCE[0]}")/steps.sh"
steps=(isolated-consumers angular-outlet window-factory navigation-consumer operations-consumer wpf-bridge-consumer standalone-navigation reactiveui-navigation)

step_isolated_consumers() { bun run verify-packages; }
step_angular_outlet() {
  local version
  version=$(bun -p 'JSON.parse(require("node:fs").readFileSync("eng/workspace.json", "utf8")).version')
  bun tests/web/angular-package-consumer/test-package-consumer.mjs "artifacts/packages/npm/runic-artifex-angular-$version.tgz"
}
step_window_factory() {
  bun run --cwd packages/web/views --bun build
  node examples/first-window/package-smoke.mjs
  node examples/first-window-desktop/package-smoke.mjs
}
step_navigation_consumer() { bun tests/fixtures/application/navigation-consumer/package-smoke.mjs; }
step_operations_consumer() { bun tests/fixtures/application/operations-consumer/package-smoke.mjs; }
step_wpf_bridge_consumer() { bun tests/fixtures/application/wpf-bridge-consumer/package-smoke.mjs; }
step_standalone_navigation() { bun tests/fixtures/navigation/standalone-consumer/package-smoke.mjs; }
step_reactiveui_navigation() { bun tests/fixtures/navigation/reactiveui-consumer/package-smoke.mjs; }

run_steps "$@"
