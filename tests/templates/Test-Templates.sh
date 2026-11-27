#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "Usage: $0 <package-version> <package-directory> <runic-npm-archive.tgz>..." >&2
  exit 2
fi

package_version="$1"
package_directory="$(cd "$2" && pwd)"
npm_archives=()
for archive in "${@:3}"; do npm_archives+=("$(realpath "$archive")"); done
script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_directory/../.." && pwd)"
# The Runic Translations release the template pins literally (--translations): the repository's own pin.
translations_version="$(sed -n 's:.*<PackageVersion Include="Runic.Translations" Version="\([^"]*\)".*:\1:p' "$repository_root/Directory.Packages.props")"
template_package="$package_directory/Runic.Application.Templates.$package_version.nupkg"
template_tmp="$(mktemp -d /tmp/runic-views-templates.XXXXXXXXXX)"
registry_pid=""
# The process that keeps the shared virtual display open, if one was started.
display_holder_pid=""
display_holder_child=""
# Process group of a running serve-only application or dotnet runic dev.
served_group=""

# Processes in a group, without the Roslyn and MSBuild build servers that a
# build starts and that outlive it by design.
started_processes() {
  ps -o pid=,args= -g "$1" | grep -vE 'VBCSCompiler|MSBuild\.dll.*/nodemode:' || true
}

stop_served() {
  if [[ -n "$served_group" ]]; then
    kill -TERM -- "-$served_group" 2>/dev/null || true
    wait "$served_group" 2>/dev/null || true
    served_group=""
  fi
}

cleanup() {
  stop_served
  if [[ -n "$display_holder_pid" ]]; then
    # Ending the holder lets xvfb-run stop Xvfb; ending xvfb-run itself would leave Xvfb running.
    if [[ -n "$display_holder_child" ]]; then
      kill "$display_holder_child" 2>/dev/null || true
    else
      kill "$display_holder_pid" 2>/dev/null || true
    fi
    wait "$display_holder_pid" 2>/dev/null || true
  fi
  if [[ -n "$registry_pid" ]]; then
    kill "$registry_pid" 2>/dev/null || true
    wait "$registry_pid" 2>/dev/null || true
  fi
  case "$template_tmp" in
    /tmp/runic-views-templates.*) rm -rf -- "$template_tmp" ;;
    *) echo "Refusing to remove unexpected path: $template_tmp" >&2 ;;
  esac
}
trap cleanup EXIT

for required in "$template_package" "${npm_archives[@]}"; do
  if [[ ! -f "$required" ]]; then
    echo "Required template acceptance input is missing: $required" >&2
    exit 1
  fi
done

# On Linux, WebUI loads GTK 3 and WebKitGTK 4.1 when it creates a CS-WebUI
# window, if both are installed, and calls gtk_init, which ends the process
# when no display opens, also with RUNIC_APPLICATION_SERVE_ONLY. Without a
# display, such as in the CI lane that installs WebKitGTK for the Desktop
# smoke, every application therefore shares one virtual display.
if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]] && command -v xvfb-run > /dev/null; then
  display_file="$template_tmp/virtual-display"
  # The holder prints its PID; ending it lets xvfb-run stop Xvfb and remove its authority file.
  xvfb-run -a bash -c 'printf "%s %s %s\n" "$$" "$DISPLAY" "$XAUTHORITY" > "$1"; exec sleep infinity' _ "$display_file" &
  display_holder_pid=$!
  for _ in $(seq 1 300); do
    [[ -s "$display_file" ]] && break
    sleep 0.1
  done
  [[ -s "$display_file" ]]
  read -r display_holder_child DISPLAY XAUTHORITY < "$display_file"
  export DISPLAY XAUTHORITY
fi

export DOTNET_CLI_HOME="$template_tmp/dotnet-home"
export NUGET_PACKAGES="$template_tmp/nuget"
export BUN_INSTALL_CACHE_DIR="$template_tmp/bun-cache"
export PNPM_CONFIG_STORE_DIR="$template_tmp/pnpm-store"
restore_sources=(--source "$package_directory" --source https://api.nuget.org/v3/index.json)
tool_restore_options=()
tool_source_options=(--add-source "$package_directory")
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  restore_sources=(--configfile "$NUGET_CONFIG_FILE")
  tool_restore_options=(--configfile "$NUGET_CONFIG_FILE")
  tool_source_options=()
elif [[ -n "${RUNIC_VERIFICATION_FEED:-}" ]]; then
  restore_sources=(--source "$RUNIC_VERIFICATION_FEED" "${restore_sources[@]}")
fi
# During a train, the unpublished Command Line and Translations releases come from the train feed
# (eng/ci/train-feed.mjs): an extra source next to the others, which also serves dotnet tool restore.
if [[ -z "${NUGET_CONFIG_FILE:-}" && -n "${RUNIC_TRAIN_FEED:-}" ]]; then
  restore_sources=(--source "$RUNIC_TRAIN_FEED" "${restore_sources[@]}")
  tool_source_options+=(--add-source "$RUNIC_TRAIN_FEED")
fi

# The documented commands take no source options. A NuGet.config above the
# generated projects supplies the candidate feed to dotnet tool restore,
# dotnet runic dev, and dotnet build exactly as a user's configuration would.
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  cp -- "$NUGET_CONFIG_FILE" "$template_tmp/NuGet.config"
else
  {
    echo '<?xml version="1.0" encoding="utf-8"?>'
    echo '<configuration>'
    echo '  <packageSources>'
    echo '    <clear />'
    if [[ -n "${RUNIC_VERIFICATION_FEED:-}" && "$(realpath "$RUNIC_VERIFICATION_FEED")" != "$package_directory" ]]; then
      echo "    <add key=\"runic-verification\" value=\"$RUNIC_VERIFICATION_FEED\" />"
    fi
    echo "    <add key=\"runic-candidate\" value=\"$package_directory\" />"
    if [[ -n "${RUNIC_TRAIN_FEED:-}" ]]; then
      echo "    <add key=\"runic-train-feed\" value=\"$RUNIC_TRAIN_FEED\" />"
    fi
    echo '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
    echo '  </packageSources>'
    echo '</configuration>'
  } > "$template_tmp/NuGet.config"
fi

dotnet new install "$template_package" --force
# --host cswebui references the preview Runic.Application.Views.CsWebUi package, which ships on its own
# prerelease version (eng/release/README.md, "Package tiers"); the template pack stamps that version separately.
# The template has no version options: the candidate template is packed with the versions it references
# (-p:RunicNuGetTemplateVersion=..., -p:RunicCsWebUiNuGetTemplateVersion=..., -p:RunicNpmTemplateVersion=...),
# and each cswebui variant restores the stamped version from the candidate feed.
cswebui_packages=("$package_directory"/Runic.Application.Views.CsWebUi.*.nupkg)
if [[ ${#cswebui_packages[@]} -ne 1 || ! -f "${cswebui_packages[0]}" ]]; then
  echo "Expected one Runic.Application.Views.CsWebUi package in $package_directory." >&2
  exit 1
fi
cswebui_version="$(basename "${cswebui_packages[0]}" .nupkg)"
cswebui_version="${cswebui_version#Runic.Application.Views.CsWebUi.}"
tool_directory="$template_tmp/tools"
dotnet tool install dotnet-runic \
  --tool-path "$tool_directory" \
  --version "$package_version" \
  "${tool_source_options[@]}" \
  "${tool_restore_options[@]}"

npm_version="$(bun "$repository_root/eng/toolchain.mjs" npm)"
pnpm_version="$(bun "$repository_root/eng/toolchain.mjs" pnpm)"
bun_version="$(bun "$repository_root/eng/toolchain.mjs" bun)"
package_manager_directory="$template_tmp/package-managers"
npm install --global --prefix "$package_manager_directory" "pnpm@$pnpm_version" \
  --allow-scripts=pnpm --no-audit --no-fund
export PATH="$package_manager_directory/bin:$PATH"
[[ "$(cd "$template_tmp" && pnpm --version)" == "$pnpm_version" ]]
[[ "$(bun --version)" == "$bun_version" ]]

# The browser check (run_browser_check) opens each frontend's production page in Chromium under its real
# Content-Security-Policy and fails on any violation. RUNIC_TEMPLATE_BROWSER_CHECK=1 requires it (CI), 0 skips it,
# and auto, the default, runs it when PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH names a browser.
browser_check="${RUNIC_TEMPLATE_BROWSER_CHECK:-auto}"
case "$browser_check" in
  auto) [[ -n "${PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH:-}" ]] && browser_check=1 || browser_check=0 ;;
  0|1) ;;
  *) echo "RUNIC_TEMPLATE_BROWSER_CHECK must be 0, 1 or auto." >&2; exit 2 ;;
esac
if [[ "$browser_check" == 1 ]]; then
  # The collector must see a violation before its silence can pass a page.
  bun "$script_directory/check-template-page.mjs" --self-check
else
  echo "BROWSER_CSP_SKIPPED|no Chromium (set PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH or RUNIC_TEMPLATE_BROWSER_CHECK=1)"
fi

npm_archive_version() {
  bun -e '
    const { execFileSync } = require("node:child_process");
    const manifest = JSON.parse(execFileSync("tar", ["-xOf", process.argv[1], "package/package.json"], { encoding: "utf8" }));
    process.stdout.write(manifest.version);
  ' "$1"
}

# Runic npm packages share one release-train version.
views_npm_version="$(npm_archive_version "${npm_archives[0]}")"
registry_ready="$template_tmp/template-npm-registry.url"
# During a train, the registry also serves the train feed's npm candidates of the independently released Runic packages
# (the Translations Vite plugin of --translations); npm serves the published ones.
mapfile -t train_npm_archives < <(cd "$repository_root" && bun -e '
  import { trainFeedNpm } from "./eng/ci/train-feed.mjs";
  for (const item of Object.values(trainFeedNpm())) console.log(item.file);
')
bun "$script_directory/template-npm-registry.mjs" \
  "$registry_ready" "${npm_archives[@]}" "${train_npm_archives[@]}" &
registry_pid=$!
for _ in $(seq 1 100); do
  [[ -s "$registry_ready" ]] && break
  sleep 0.05
done
[[ -s "$registry_ready" ]]
registry_url="$(<"$registry_ready")"

# Every template installs the Views runtime and its framework binding.
configure_candidate_registry() {
  local manager="$1"
  local output="$2"
  (cd "$output/Frontend" && npm config set --location=project @runic-artifex:registry "$registry_url")
  # I-167: npm waits 5 minutes for a stalled registry response before it retries, as long as the whole
  # URL wait below. A minute lets its retries (fetch-retries, 2 by default) recover from one stall.
  (cd "$output/Frontend" && npm config set --location=project fetch-timeout 60000)
  # Every manager's lock is checked against the candidates and takes the train candidates' integrities: a pnpm or
  # Bun lock that kept the committed hash would fail on any newer upstream CI run of the same version.
  local lock
  case "$manager" in
    npm) lock=package-lock.json ;;
    pnpm) lock=pnpm-lock.yaml ;;
    bun) lock=bun.lock ;;
  esac
  RUNIC_TEMPLATE_NPM_REGISTRY="$registry_url" bun "$script_directory/bind-template-candidate-integrities.mjs" \
    "$output/Frontend/$lock" "${npm_archives[@]}" "${train_npm_archives[@]}"
}

frontend_script() {
  local manager="$1"
  local frontend="$2"
  local script="$3"
  case "$manager" in
    npm) (cd "$frontend" && npm run "$script") ;;
    pnpm) (cd "$frontend" && pnpm run "$script") ;;
    bun) (cd "$frontend" && bun run --bun "$script") ;;
  esac
}

# Whether dotnet runic dev's log shows its frontend dependency install running.
installing_frontend() {
  grep -Fq '[dev] Installing Views Window frontend dependencies...' "$1" \
    && ! grep -Eq '^\[dev\] Installing Views Window frontend dependencies (completed|failed)' "$1"
}

# Starts a command in its own process group with RUNIC_APPLICATION_SERVE_ONLY,
# waits for the application's URL, and fetches the served document. The
# development document points at the frontend development server; a production
# document does not.
serve_and_fetch() {
  local directory="$1"
  local log="$2"
  local document="$3"
  local expect_development="$4"
  shift 4
  local started=$SECONDS
  setsid env -C "$directory" RUNIC_APPLICATION_SERVE_ONLY=1 "$@" > "$log" 2>&1 < /dev/null &
  served_group=$!
  local url=""
  # I-167: dotnet runic dev installs the frontend dependencies from the registries before it starts
  # anything. The install has its own 10 minutes; restore, build and start keep 5 minutes besides.
  local waited=0 install_limit=1200 install_waited=0
  while (( waited < 600 )); do
    url="$(grep -o 'RUNIC_APPLICATION_URL=[^[:space:]]*' "$log" | head -n 1 | cut -d= -f2- || true)"
    [[ -n "$url" ]] && break
    if ! kill -0 "$served_group" 2>/dev/null; then break; fi
    sleep 0.5
    if installing_frontend "$log"; then
      (( install_waited += 1 ))
      (( install_waited < install_limit )) || break
    else
      (( waited += 1 ))
    fi
  done
  if [[ -z "$url" ]]; then
    local stage
    stage="$(grep -E '^\[dev\] ' "$log" | tail -n 1 || true)"
    echo "The application did not report its URL within $((SECONDS - started)) s (the frontend install took $((install_waited / 2)) s; last stage: ${stage:-none}): $*" >&2
    tail -n 80 "$log" >&2
    # npm writes what each request waited for to its debug log, which dev does not show before the install ends.
    local npm_log
    npm_log="$(ls -t "${npm_config_cache:-$HOME/.npm}"/_logs/*-debug-0.log 2>/dev/null | head -n 1 || true)"
    if [[ -n "$npm_log" ]] && installing_frontend "$log"; then
      echo "The newest npm debug log ($npm_log):" >&2
      tail -n 40 "$npm_log" >&2
    fi
    exit 1
  fi
  # WebUI admits one client and identifies it by the cookie set on the first
  # response, so later requests must send it back.
  local cookies="$document.cookies"
  # Each fetch and check names what failed and shows the answer and the log, instead of ending the script silently.
  fetch_served() {
    if ! curl -fsS -D "$2.headers" -c "$cookies" -b "$cookies" "$url/$1" > "$2"; then
      echo "Fetching $url/$1 from the served application failed: $*" >&2
      cat "$2.headers" >&2 2>/dev/null || true
      tail -n 80 "$log" >&2
      exit 1
    fi
  }
  expect_served() {
    if ! grep -Fq "$1" "$2"; then
      echo "The served $3 does not contain '$1'. Headers and start of the answer:" >&2
      cat "$2.headers" >&2 2>/dev/null || true
      head -c 2000 "$2" >&2
      echo >&2
      tail -n 80 "$log" >&2
      exit 1
    fi
  }
  fetch_served index.html "$document"
  fetch_served runic-views.js "$document.runic-views.js"
  expect_served 'CS-WebUI owns the browser transport' "$document.runic-views.js" runic-views.js
  # A second window's page is served from the same web root as index.html.
  local pages=("$document")
  if [[ -f "$directory/Frontend/settings.html" ]]; then
    fetch_served settings.html "$document.settings.html"
    expect_served '<title>Second window</title>' "$document.settings.html" settings.html
    pages+=("$document.settings.html")
  fi
  if [[ "$expect_development" == true ]]; then
    local page
    for page in "${pages[@]}"; do expect_served 'http://127.0.0.1:' "$page" "development document $page"; done
    # served_module (verify_library_template): a page module that the development server must transform, with the text
    # its answer must contain, such as the resolved path of a workspace package it imports.
    if [[ -n "${served_module:-}" ]]; then
      local origin
      origin="$(grep -o 'http://127\.0\.0\.1:[0-9]*' "$document" | head -n 1)"
      if ! curl -fsS -D "$document.module.headers" "$origin/$served_module" > "$document.module"; then
        echo "The development server at $origin did not serve $served_module." >&2
        head -c 2000 "$document.module" >&2 || true
        tail -n 80 "$log" >&2
        exit 1
      fi
      expect_served "$served_module_expect" "$document.module" "module $served_module"
    fi
    # An IDE or terminal may end only dotnet runic dev; it must stop the
    # frontend and native host processes it started.
    kill -TERM "$served_group"
    for _ in $(seq 1 60); do
      [[ -z "$(started_processes "$served_group")" ]] && break
      sleep 0.5
    done
    if [[ -n "$(started_processes "$served_group")" ]]; then
      echo "dotnet runic dev left processes running after SIGTERM:" >&2
      started_processes "$served_group" >&2
      exit 1
    fi
  else
    if grep -Fq 'http://127.0.0.1:' "${pages[@]}"; then
      echo "The production build served a development document." >&2
      exit 1
    fi
  fi
  stop_served
}

# Per-phase timing (C3 of D077.25): `phase <name>` ends the running phase and starts <name>; phase_report prints one
# TEMPLATE_PHASES line per combination with the seconds of each phase and, in CI, adds a row to the step summary.
phase_name=""
phase_started=""
phase_times=()
phase_microseconds() { local now="${EPOCHREALTIME//[.,]/}"; echo "$((10#$now))"; }
phase() {
  local now
  now="$(phase_microseconds)"
  if [[ -n "$phase_name" ]]; then
    local tenths=$(( (now - phase_started) / 100000 ))
    phase_times+=("$phase_name=$((tenths / 10)).$((tenths % 10))")
  fi
  phase_name="$1"
  phase_started="$now"
}
phase_report() {
  local combination="$1" total=0 item tenths
  phase ""
  for item in "${phase_times[@]}"; do
    tenths="${item#*=}"
    total=$(( total + ${tenths%.*} * 10 + ${tenths#*.} ))
  done
  local joined
  joined="$(IFS='|'; echo "${phase_times[*]}")"
  echo "TEMPLATE_PHASES|$combination|total=$((total / 10)).$((total % 10))|$joined"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    if [[ "${phase_summary_header:-}" != written ]]; then
      printf '\n| Template combination | Seconds | Phases (s) |\n|---|---|---|\n' >> "$GITHUB_STEP_SUMMARY"
      phase_summary_header=written
    fi
    printf '| %s | %s | %s |\n' "$combination" "$((total / 10)).$((total % 10))" "${joined//|/, }" >> "$GITHUB_STEP_SUMMARY"
  fi
  phase_name=""
  phase_times=()
}

verify_template() {
  local framework="$1"
  local manager="$2"
  local host="$3"
  local view_models="$4"
  # Trailing flags: "defaults" omits --host and --view-models, so the checks
  # below prove that the template's defaults are this host and ViewModel
  # library; "tests" adds the TUnit test project and runs it; "gtk4" adds
  # --linux-gtk 4 to the desktop host; "translations" adds --translations.
  local defaults="" tests="" restore="" translations="" linux_gtk=3 flag
  for flag in "${@:5}"; do
    case "$flag" in
      defaults) defaults=defaults ;;
      tests) tests=tests ;;
      restore) restore=restore ;;
      gtk4) linux_gtk=4 ;;
      translations) translations=translations ;;
      *) echo "Unknown verify_template flag: $flag" >&2; exit 2 ;;
    esac
  done
  if [[ "$linux_gtk" == 4 && ( "$host" != desktop || "$defaults" == defaults ) ]]; then
    echo "The gtk4 flag needs an explicit desktop host." >&2
    exit 2
  fi
  local selection=(--host "$host" --view-models "$view_models")
  [[ "$linux_gtk" == 4 ]] && selection+=(--linux-gtk 4)
  [[ "$defaults" == defaults ]] && selection=()
  local tests_options=()
  [[ "$tests" == tests ]] && tests_options=(--tests)
  [[ "$restore" == restore ]] && tests_options+=(--restore)
  [[ "$translations" == translations ]] && tests_options+=(--translations)
  # angular-vite is the Angular frontend built with Vite and the Analog plugin (--angular-build vite).
  local frontend_options=(--frontend "$framework") framework_name="${framework^}"
  if [[ "$framework" == angular-vite ]]; then frontend_options=(--frontend angular --angular-build vite); framework_name=AngularVite; fi
  # SvelteKit renders its page from src/app.html and its routes, and the Angular CLI from src/index.html; the other
  # Vite frontends, Angular with Vite too, build HTML inputs.
  local html_inputs=true
  [[ "$framework" == angular || "$framework" == sveltekit ]] && html_inputs=false
  # --host desktop --linux-gtk 4 names the project ...DesktopGtk4... and its directory ...-desktop-gtk4-....
  local host_label="$host" host_name="${host^}"
  if [[ "$linux_gtk" == 4 ]]; then host_label+=-gtk4; host_name+=Gtk4; fi
  local project_name="Acceptance${framework_name}${manager^}${host_name}${view_models^}"
  local output="$template_tmp/$framework-$manager-$host_label-$view_models"
  if [[ "$translations" == translations ]]; then project_name+=Translations; output+=-translations; fi
  # The starter declares the pinned version in packageManager; the active npm may differ: a lane on Node 26 keeps the
  # npm 11 that Node ships, which doctor supports with a warning (#1088).
  local expected_manager_version declared_manager_version
  local selected_lock
  case "$manager" in
    npm) expected_manager_version="$(npm --version)"; selected_lock=package-lock.json ;;
    pnpm) expected_manager_version="$pnpm_version"; selected_lock=pnpm-lock.yaml ;;
    bun) expected_manager_version="$bun_version"; selected_lock=bun.lock ;;
  esac
  declared_manager_version="$expected_manager_version"
  [[ "$manager" != npm ]] || declared_manager_version="$npm_version"

  phase generate
  dotnet new runic-app \
    --name "$project_name" \
    --output "$output" \
    "${frontend_options[@]}" \
    --package-manager "$manager" \
    "${selection[@]}" \
    "${tests_options[@]}"

  # The opt-in restore post-action restores the created projects, which writes their NuGet lock files (SEC-31).
  if [[ "$restore" == restore ]]; then
    test -f "$output/obj/project.assets.json"
    test -f "$output/packages.lock.json"
    [[ "$tests" != tests ]] || test -f "$output/$project_name.Tests/packages.lock.json"
  elif [[ -e "$output/obj" ]]; then
    echo "The $framework project without --restore was restored." >&2
    exit 1
  fi
  grep -Fq '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>' "$output/$project_name.csproj"

  # The candidate template references the candidate packages: the pack stamped their versions.
  if ! grep -Fq "\"version\": \"$package_version\"" "$output/.config/dotnet-tools.json" \
    || grep -E 'Include="Runic\.[^"]*" Version=' "$output/$project_name.csproj" \
      | grep -vF "Version=\"$package_version\"" | grep -vF "Include=\"Runic.Application.Views.CsWebUi\" Version=\"$cswebui_version\"" \
      | grep -vE "Include=\"Runic\\.Translations(\\.Build)?\" Version=\"${translations_version//./\\.}\""; then
    echo "The template does not reference the Runic $package_version packages; pack it with -p:RunicNuGetTemplateVersion=$package_version." >&2
    exit 1
  fi
  if ! grep -Fq "\"@runic-artifex/views\": \"$views_npm_version\"" "$output/Frontend/package.json"; then
    echo "The template does not reference the Runic $views_npm_version npm packages; pack it with -p:RunicNpmTemplateVersion=$views_npm_version." >&2
    exit 1
  fi

  test -f "$output/Frontend/$selected_lock"
  for other_lock in package-lock.json pnpm-lock.yaml bun.lock; do
    [[ "$other_lock" == "$selected_lock" ]] || test ! -f "$output/Frontend/$other_lock"
  done
  if ! grep -Fq "\"packageManager\": \"$manager@$declared_manager_version\"" "$output/Frontend/package.json"; then
    echo "The $framework starter does not declare packageManager $manager@$declared_manager_version." >&2
    exit 1
  fi
  # The frontend package is named after the project, not the template.
  local package_name
  package_name="$(sed -E -e 's/([a-z0-9])([A-Z])/\1-\2/g' -e 's/([a-zA-Z])([0-9])/\1-\2/g' <<< "$project_name" | tr '[:upper:]' '[:lower:]')"
  grep -Fq "\"name\": \"$package_name\"" "$output/Frontend/package.json"
  grep -Fq 'RunicApplicationFrontendWindowProject>true' "$output/$project_name.csproj"
  # A Svelte config keeps vite-plugin-svelte from reporting a missing config on every build.
  [[ "$framework" != svelte ]] || grep -Fq 'vitePreprocess()' "$output/Frontend/svelte.config.js"
  # One Window declaration and one application body serve every host and MVVM library.
  grep -Fq 'public sealed partial class WorkspaceWindow : RunicWindow<WorkspaceViewModel>;' "$output/Views.cs"
  if grep -nE 'Views\.(Desktop|CsWebUi)|(Desktop|CsWebUi)BridgeWindow|CsWebUiWindow' "$output/Views.cs"; then
    echo "The $host Window declaration depends on its host." >&2
    exit 1
  fi
  grep -Fq 'await using var window = await host.OpenWindowAsync<WorkspaceWindow>(' "$output/Program.cs"
  grep -Fq 'await window.WaitForCloseAsync();' "$output/Program.cs"
  local index_html="$output/Frontend/index.html"
  [[ "$framework" == angular ]] && index_html="$output/Frontend/src/index.html"
  [[ "$framework" == sveltekit ]] && index_html="$output/Frontend/src/app.html"
  # The page names no host: every host serves its Views client as runic-views.js (#237).
  grep -Fq '<script src="runic-views.js"></script>' "$index_html"
  if grep -nE 'runic-(cswebui|desktop-views|desktop)\.js' "$index_html"; then
    echo "The $host frontend names a host script." >&2
    exit 1
  fi
  if [[ "$framework" == sveltekit ]]; then
    grep -Fq 'runicAdapter({ out: "dist" })' "$output/Frontend/vite.config.ts"
    grep -Fq "\"@runic-artifex/sveltekit\": \"$views_npm_version\"" "$output/Frontend/package.json"
  fi
  # Every starter loads the Runic development client while serving: runic() injects it, SvelteKit's client hooks
  # import it for runic({ html: false }), and Angular's main.ts imports it outside production builds.
  case "$framework" in
    sveltekit)
      grep -Fq 'runic({ html: false })' "$output/Frontend/vite.config.ts"
      grep -Fq 'import "@runic-artifex/vite-plugin-runic/client";' "$output/Frontend/src/hooks.client.ts"
      ;;
    angular)
      grep -Fq '...runicDevSessionProxy(),' "$output/Frontend/proxy.conf.mjs"
      grep -Fq 'if (typeof ngDevMode === "undefined" || ngDevMode) await import("@runic-artifex/vite-plugin-runic/client");' \
        "$output/Frontend/src/main.ts"
      grep -Fq '"build": "ng build frontend"' "$output/Frontend/package.json"
      if [[ -e "$output/Frontend/vite.config.ts" ]] || grep -Fq '@analogjs/' "$output/Frontend/package.json"; then
        echo "The Angular CLI project has the Vite build's files." >&2
        exit 1
      fi
      ;;
    angular-vite)
      # Vite builds the page with the Analog plugin and runic(), which injects the development client; no Angular CLI
      # files remain, so dotnet runic dev serves it with Vite.
      grep -Fq 'angular({ liveReload: true }),' "$output/Frontend/vite.config.ts"
      grep -Fq '"build": "vite build && ngc --project tsconfig.app.json --noEmit"' "$output/Frontend/package.json"
      grep -Fq '"@analogjs/vite-plugin-angular": "2.9.7"' "$output/Frontend/package.json"
      if [[ -e "$output/Frontend/angular.json" || -e "$output/Frontend/proxy.conf.mjs" || -e "$output/Frontend/src/index.html" ]] \
        || grep -Fq 'vite-plugin-runic/client' "$output/Frontend/src/main.ts" || grep -Fq '@angular/cli' "$output/Frontend/package.json"; then
        echo "The Angular project built with Vite has the Angular CLI's files." >&2
        exit 1
      fi
      ;;
  esac
  if [[ "$framework" != angular ]]; then
    [[ "$framework" == sveltekit ]] || grep -Fq 'runic()' "$output/Frontend/vite.config.ts"
    if grep -n 'desktop' "$output/Frontend/vite.config.ts"; then
      echo "The $host Vite configuration chooses a host." >&2
      exit 1
    fi
  fi
  # D072.11: the host's Views client is copied only as runic-views.js; 0.23 removed the host-named copy.
  local host_client host_named_client
  case "$host" in
    cswebui)
      host_client='CS-WebUI owns the browser transport'
      host_named_client=runic-cswebui.js
      grep -Fq "<PackageReference Include=\"Runic.Application.Views.CsWebUi\" Version=\"$cswebui_version\" />" "$output/$project_name.csproj"
      grep -Fq 'return RunicCsWebUiHost.Run(provider, RunAsync);' "$output/Program.cs"
      ;;
    desktop)
      host_client='The Desktop surface serves webui.js'
      host_named_client=runic-desktop-views.js
      grep -Fq 'Runic.Application.Views.Desktop' "$output/$project_name.csproj"
      grep -Fq 'return RunicDesktopHost.Run(provider, options, hostOptions, RunAsync);' "$output/Program.cs"
      # SEC-06 and D040: a Content-Security-Policy outside Development, without 'unsafe-eval': the Bridge needs none.
      grep -Fq "ContentSecurityPolicy = developing ? null : contentSecurityPolicy" "$output/Program.cs"
      grep -Fq "script-src 'self'" "$output/Program.cs"
      if grep -Fq "'unsafe-eval'" "$output/Program.cs"; then
        echo "The generated Content-Security-Policy allows 'unsafe-eval'." >&2
        exit 1
      fi
      grep -Fq "object-src 'none'; base-uri 'self'; frame-ancestors 'none'\";" "$output/Program.cs"
      grep -Fq 'DiagnosticSink = ReportDiagnostic' "$output/Program.cs"
      ;;
  esac
  # Every host registers the platform-services facade, and no project picks a provider package itself.
  grep -Fq 'services.AddRunicPlatformServices();' "$output/WorkspaceServices.cs"
  if grep -nE 'NativeOwner|PlatformProvider|Runic\.Platform\.' "$output/Views.cs" "$output/Program.cs" "$output/WorkspaceServices.cs" "$output/$project_name.csproj"; then
    echo "The $host project chooses a platform provider instead of using AddRunicPlatformServices." >&2
    exit 1
  fi
  # Only the GTK 4 variant selects GTK 4 and references its desktop package.
  if [[ "$linux_gtk" == 4 ]]; then
    grep -Fq 'DiagnosticSink = ReportDiagnostic, MenuBar = menuBar }.WithGtk4(ApplicationIdentity.Id);' "$output/Program.cs"
    grep -Fq '"Runic.Desktop.Gtk4"' "$output/$project_name.csproj"
  elif grep -nE 'Gtk4|Linux\.Portal' "$output/Program.cs" "$output/$project_name.csproj"; then
    echo "The $host project references the GTK 4 profile." >&2
    exit 1
  fi
  case "$view_models" in
    toolkit) grep -Fq 'CommunityToolkit.Mvvm' "$output/$project_name.csproj" ;;
    reactiveui) grep -Fq 'Runic.Application.Views.ReactiveUI' "$output/$project_name.csproj" ;;
  esac
  local tests_project="$output/$project_name.Tests/$project_name.Tests.csproj"
  if [[ "$tests" == tests ]]; then
    # The template pins the repository's TUnit version, and dotnet test needs
    # Microsoft.Testing.Platform mode on the .NET 10 SDK.
    local tunit_version
    tunit_version="$(grep -o 'Include="TUnit" Version="[^"]*"' "$repository_root/Directory.Packages.props" | cut -d'"' -f4)"
    [[ -n "$tunit_version" ]]
    grep -Fq "<PackageReference Include=\"TUnit\" Version=\"$tunit_version\" />" "$tests_project"
    grep -Fq 'AdditionalProperties="RunicApplicationFrontendBuildEnabled=false"' "$tests_project"
    grep -Fq '"runner": "Microsoft.Testing.Platform"' "$output/global.json"
    grep -Fq "$project_name.Tests/**" "$output/$project_name.csproj"
  elif [[ -e "$output/$project_name.Tests" || -e "$output/global.json" ]] \
    || grep -Fq '.Tests/**' "$output/$project_name.csproj"; then
    echo "The $framework project without --tests contains the test project." >&2
    exit 1
  fi
  if grep -rnIE --exclude='*lock*' '^[[:space:]]*#(if|elif|else|endif)\b|<!--#|__[A-Z][A-Z_]+__' "$output"; then
    echo "The generated $framework project contains unprocessed template syntax." >&2
    exit 1
  fi
  # The Window project switch is the one Runic build setting the template declares, and --translations opts in to
  # RunicApplicationFrontendTranslations, which is off by default.
  if grep -vE 'RunicApplicationFrontend(WindowProject|Translations)>' "$output/$project_name.csproj" | grep -Eq 'RunicApplicationFrontend|RunicBridge|RunicViewsWindowProject'; then
    echo "The generated $framework project declares settings the Runic packages default." >&2
    exit 1
  fi
  if grep -rniIE 'Runic\.Application\.Bridge|application-bridge|@runic-artifex/(views-angular|views-svelte|desktop)' "$output"; then
    echo "The generated $framework template contains a removed Bridge or Desktop package." >&2
    exit 1
  fi
  if [[ "$host" == cswebui ]] && grep -rniI 'Runic\.Desktop' "$output"; then
    echo "The generated CS-WebUI project references Runic Desktop." >&2
    exit 1
  fi
  configure_candidate_registry "$manager" "$output"
  # A second window gets its own page as a second Vite build input, the step the
  # template's vite.config.ts describes. Angular builds through the Angular CLI
  # and keeps a single page here.
  if [[ "$html_inputs" == true ]]; then
    sed -i 's|input: \["index.html"\]|input: ["index.html", "settings.html"]|' \
      "$output/Frontend/vite.config.ts"
    grep -Fq 'input: ["index.html", "settings.html"]' "$output/Frontend/vite.config.ts"
  fi

  # The documented first run, verbatim: dotnet tool restore, then
  # dotnet runic dev. doctor must already work before anything is restored.
  phase doctor-and-dev
  (cd "$output" && dotnet tool restore)
  if ! (cd "$output" && dotnet runic doctor) > "$output/doctor-unrestored.txt"; then
    cat "$output/doctor-unrestored.txt" >&2
    exit 1
  fi
  # The restore post-action already restored a --restore project.
  if [[ "$restore" == restore ]]; then
    grep -Fq 'PASS compatibility-set:' "$output/doctor-unrestored.txt"
  else
    grep -Fq 'WARN compatibility-set: The project has not been restored yet' "$output/doctor-unrestored.txt"
  fi
  # The browser check follows the host, before restore too; Desktop projects
  # get no CS-WebUI advice.
  if [[ "$host" == desktop* ]]; then
    grep -Fq 'PASS browser: Not required' "$output/doctor-unrestored.txt"
    if grep -Fq 'CS-WebUI' "$output/doctor-unrestored.txt"; then
      cat "$output/doctor-unrestored.txt" >&2
      exit 1
    fi
  fi
  if [[ "$html_inputs" == true ]]; then
    sed 's|<title>[^<]*</title>|<title>Second window</title>|' "$output/Frontend/index.html" > "$output/Frontend/settings.html"
  fi
  # serve_and_fetch also fetches CS-WebUI's runic-views.js. Desktop variants
  # open a native window; the smoke lane opens them under Xvfb (run_desktop_smoke).
  if [[ "$manager" == npm && "$host" == cswebui ]]; then
    serve_and_fetch "$output" "$output/dotnet-runic-dev.log" "$output/dev-document.html" true \
      dotnet runic dev
    # Every starter's development server confirms the dev session (runic(), runic({ html: false }) for SvelteKit,
    # runicDevSessionProxy() in the Angular proxyConfig), so dev never takes another process on its port for it.
    if ! grep -Fq 'development server confirmed this dev session.' "$output/dotnet-runic-dev.log"; then
      echo "The $framework development server did not confirm the dev session." >&2
      tail -n 80 "$output/dotnet-runic-dev.log" >&2
      exit 1
    fi
    # Without angular.json, dotnet runic dev serves an Angular page with Vite (RunicApplicationFrontendDevServerKind).
    if [[ "$framework" == angular-vite ]] && ! grep -Fq 'Vite development server ready at' "$output/dotnet-runic-dev.log"; then
      echo "dotnet runic dev did not serve the Angular page with Vite." >&2
      tail -n 80 "$output/dotnet-runic-dev.log" >&2
      exit 1
    fi
  fi
  if [[ "$manager" == npm && "$host" == cswebui && "$html_inputs" == true ]]; then
    # A window entry that is a build input but has no file fails before the
    # build, naming the page instead of timing out the window's connection.
    mv "$output/Frontend/settings.html" "$output/settings.html.kept"
    if (cd "$output" && RUNIC_APPLICATION_SERVE_ONLY=1 timeout -k 10 300 dotnet runic dev --no-restore) \
      > "$output/dotnet-runic-dev-missing.log" 2>&1 < /dev/null; then
      echo "dotnet runic dev accepted a missing window entry." >&2
      exit 1
    fi
    if ! grep -Fq "Window entry not found: 'settings.html'" "$output/dotnet-runic-dev-missing.log" ||
      ! grep -Fq "Help for RAPPDEV1009:" "$output/dotnet-runic-dev-missing.log"; then
      tail -n 80 "$output/dotnet-runic-dev-missing.log" >&2
      exit 1
    fi
    mv "$output/settings.html.kept" "$output/Frontend/settings.html"
  fi

  phase restore
  dotnet restore "$output/$project_name.csproj" "${restore_sources[@]}"
  test -f "$output/packages.lock.json"
  # The runic-view item template adds a View and a Window with their ViewModels to the restored project, in its
  # RootNamespace; the build generates their clients.
  local root_namespace
  root_namespace="$(sed -n 's|^namespace \(.*\);$|\1|p' "$output/Views.cs")"
  (cd "$output" && dotnet new runic-view --name Settings --view-models "$view_models" \
    && dotnet new runic-view --name Preferences --window --view-models "$view_models") > "$output/runic-view.log"
  grep -Fq "namespace $root_namespace;" "$output/SettingsView.cs"
  grep -Fq "public sealed partial class PreferencesWindow : RunicWindow<PreferencesViewModel>;" "$output/PreferencesWindow.cs"
  grep -Fq 'services.AddScoped<NameViewModel>()' "$output/runic-view.log"
  "$tool_directory/dotnet-runic" dev \
    --project "$output/$project_name.csproj" \
    --dry-run > "$output/dotnet-runic-dev-plan.txt"
  grep -Fq 'Runic Views Window project' "$output/dotnet-runic-dev-plan.txt"

  if ! (cd "$output" && dotnet runic doctor) > "$output/doctor.txt"; then
    cat "$output/doctor.txt" >&2
    exit 1
  fi
  if [[ "$expected_manager_version" != "$declared_manager_version" ]]; then
    grep -Fq "WARN package-manager: npm $expected_manager_version is supported; certified baseline is" "$output/doctor.txt"
  else
    grep -Fq "PASS package-manager: $manager $expected_manager_version matches certified baseline" "$output/doctor.txt"
  fi
  grep -Fq "PASS compatibility-set:" "$output/doctor.txt"
  # D059.17: a lane that installs a certified newer-major SDK checks that the project, which has no global.json
  # pinning an SDK, builds with it, and that doctor certifies it and finds the .NET 10 runtime the application needs.
  if [[ -n "${RUNIC_TEMPLATE_DOTNET_SDK_MAJOR:-}" ]]; then
    sdk_version="$(cd "$output" && dotnet --version)"
    if [[ "$sdk_version" != "$RUNIC_TEMPLATE_DOTNET_SDK_MAJOR".* ]] \
      || ! grep -Fq "PASS dotnet-sdk: .NET SDK $sdk_version is certified" "$output/doctor.txt" \
      || ! grep -Fq "PASS dotnet-runtime: " "$output/doctor.txt"; then
      echo "Expected the generated project to build with a certified .NET $RUNIC_TEMPLATE_DOTNET_SDK_MAJOR SDK, not $sdk_version:" >&2
      cat "$output/doctor.txt" >&2
      exit 1
    fi
  fi
  if [[ "$host" == desktop* ]]; then
    grep -Fq 'PASS browser: Not required' "$output/doctor.txt"
    if grep -Fq 'CS-WebUI' "$output/doctor.txt"; then
      cat "$output/doctor.txt" >&2
      exit 1
    fi
  fi

  phase build
  # A plain build installs the frontend packages with the selected manager.
  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore
  # The executable's icon, written from icons/icon.png because the project sets no ApplicationIcon (sdk#1207).
  test -f "$output/obj/Release/net10.0/runic-icon/application.ico"
  test -d "$output/Frontend/node_modules"
  test -f "$output/Frontend/dist/index.html"
  # The frontend's packages and build output are not project items.
  dotnet msbuild "$output/$project_name.csproj" -getItem:None -getItem:Content -p:Configuration=Release > "$output/items.json"
  if grep -E '"Identity": "Frontend[\\/]+(node_modules|dist)[\\/]' "$output/items.json"; then
    echo "Frontend/node_modules or Frontend/dist is listed as a project item." >&2
    exit 1
  fi
  grep -Fq '"Identity": "Frontend/package.json"' "$output/items.json" || grep -Fq '"Identity": "Frontend\\package.json"' "$output/items.json"
  # #945: the production bundle links every Angular library; a partial declaration left in it fails in the browser with
  # "JIT compiler unavailable".
  if [[ "$framework" == angular* ]] && grep -rlF 'ngDeclare' "$output/Frontend/dist"; then
    echo "The $framework production bundle keeps unlinked Angular partial declarations." >&2
    exit 1
  fi
  # #954: every Vite starter registers the Vite DevTools dock (DevTools() in vite.config.ts), which only vite dev
  # serves: the production build has neither the dock, its loader nor the Runic development client.
  if [[ "$framework" != angular ]]; then
    if ! grep -Fxq '    DevTools(),' "$output/Frontend/vite.config.ts"; then
      echo "The $framework starter does not register the Vite DevTools dock." >&2
      exit 1
    fi
    if grep -rlE '__devtools|@vitejs/devtools|devframe|runic:open-in-editor|virtual:runic/client' \
      "$output/Frontend/dist" "$output/bin/Release/net10.0/www"; then
      echo "The $framework production build contains the Vite DevTools dock or the Runic development client." >&2
      exit 1
    fi
  fi
  if [[ "$html_inputs" == true ]]; then
    test -f "$output/bin/Release/net10.0/www/settings.html"
  fi
  # The referenced host's client is the page's runic-views.js, and the build copies no host-named copy (D072.11).
  grep -Fq "$host_client" "$output/bin/Release/net10.0/www/runic-views.js"
  if [[ -e "$output/bin/Release/net10.0/www/$host_named_client" ]]; then
    echo "The build copied the removed host-named client www/$host_named_client." >&2
    exit 1
  fi
  # #944: a translated Vite frontend compiles its catalog with the Translations Vite plugin, so the .NET build writes
  # no page ESM of its own, and an edited message rebuilds the page. The rebuild below runs with the restored catalog.
  if [[ "$translations" == translations && "$framework" != angular ]]; then
    if find "$output/obj" -name 'web-module-manifest-v*.json' | grep -q .; then
      echo "The $framework build wrote the page's Runic Translations ESM, which the Vite plugin owns." >&2
      exit 1
    fi
    grep -Fq 'runicTranslations()' "$output/Frontend/vite.config.ts"
    test -f "$output/Frontend/.runic/translations/virtual.d.ts"
    local catalog="$output/Frontend/translations/en.rmf2"
    cp "$catalog" "$output/en.rmf2.kept"
    sed -i 's/^  title = Runic Views$/  title = Runic Views, rebuilt/' "$catalog"
    dotnet build "$output/$project_name.csproj" --configuration Release --no-restore > "$output/rebuild-translations.log"
    if ! grep -rFq 'Runic Views, rebuilt' "$output/bin/Release/net10.0/www"; then
      echo "An edited message did not rebuild the $framework page." >&2
      exit 1
    fi
    mv "$output/en.rmf2.kept" "$catalog"
  fi
  # A changed frontend rebuilds, and www/ mirrors the new dist/: the assets of
  # the earlier build are gone, and the host scripts stay (#236).
  local entry
  entry="$(find "$output/Frontend/src" -maxdepth 1 -name 'main.ts*' | head -n 1)"
  # A SvelteKit frontend has no main module; its universal hooks are part of the client bundle.
  [[ "$framework" == sveltekit ]] && entry="$output/Frontend/src/hooks.ts"
  printf '\nconsole.info("Runic template rebuild check");\n' >> "$entry"
  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore > "$output/rebuild.log"
  local built_files served_files
  built_files="$(cd "$output/Frontend/dist" && find . -type f | sort)"
  served_files="$(cd "$output/bin/Release/net10.0/www" && find . -type f \
    ! -name runic-views.js | sort)"
  if [[ "$built_files" != "$served_files" ]]; then
    echo "www/ does not mirror Frontend/dist after a frontend change." >&2
    diff <(echo "$built_files") <(echo "$served_files") >&2 || true
    exit 1
  fi
  grep -rFq 'Runic template rebuild check' "$output/bin/Release/net10.0/www"
  phase publish
  if [[ "$html_inputs" == true && "$manager" == npm ]]; then
    dotnet publish "$output/$project_name.csproj" --configuration Release --no-restore --no-build \
      --output "$output/publish" > "$output/publish.log"
    test -f "$output/publish/www/settings.html"
    grep -Fq "$host_client" "$output/publish/www/runic-views.js"
    if [[ -e "$output/publish/www/$host_named_client" ]]; then
      echo "The publish copied the removed host-named client www/$host_named_client." >&2
      exit 1
    fi
    check_published_files "$output/publish" "$host" portable
    # D072.9: a publish for this machine's RuntimeIdentifier ships only its operating system's Runic.Platform providers.
    # Once per framework lane, with the template's defaults (the Runic Desktop host).
    local rid
    rid="$(host_runtime_identifier)"
    if [[ "$defaults" == defaults && -n "$rid" ]]; then
      dotnet publish "$output/$project_name.csproj" --configuration Release --runtime "$rid" --self-contained false \
        --output "$output/publish-$rid" > "$output/publish-$rid.log"
      check_published_files "$output/publish-$rid" "$host" "$rid"
    fi
  fi
  # Every frontend, the Angular CLI's and Vite's (angular-vite) included, bundles the production build of
  # @runic-artifex/views (README, "Development build"): its development tools, the Bridge-call channel with its async
  # stack tagging, the development features trace-context/1 and handler-timing/1 (#958), and the Translations text
  # channel, are not in the page (#975).
  if grep -rlE 'runic\.views\.calls|runic\.translations\.text|trace-context/1|handler-timing/1' "$output/Frontend/dist"; then
    echo "The built $framework frontend contains development code of @runic-artifex/views." >&2
    exit 1
  fi
  # Runic Desktop serves each Window below its own path, where root-absolute
  # asset URLs leave the page blank. Desktop variants are not started here.
  if grep -nE '(src|href)="/[^/]' "$output/Frontend/dist/index.html"; then
    echo "The built frontend document references root-absolute URLs." >&2
    exit 1
  fi
  test -f "$output/Frontend/src/generated/workspace.ts"
  test -f "$output/Frontend/src/generated/settings.ts"
  test -f "$output/Frontend/src/generated/preferences.ts"
  # R7 G6 (D080): the build wrote this variant's clients with the TypeScript emitter of the @runic-artifex/views its
  # frontend installed, the packed one; the workspace's emitter, whose output the goldens pin, writes the same bytes
  # (eng/ci/views-golden.mjs).
  bun "$repository_root/eng/ci/views-golden.mjs" --project "$output/$project_name.csproj" --configuration Release
  # One frontend build runs on every host: the built page loads runic-views.js
  # and no host-specific script, such as the Runic Desktop bootstrap.
  grep -Eq '<script\s[^>]*src="(\./)?runic-views\.js"' "$output/Frontend/dist/index.html"
  if grep -nE 'runic-(cswebui|desktop-views|desktop)\.js' "$output/Frontend/dist/index.html"; then
    echo "The built $host frontend loads a host-specific script." >&2
    exit 1
  fi
  # Every path in the README's project layout exists once the project is built.
  local layout_paths
  layout_paths="$(awk '/^## /{layout = ($0 == "## Project layout"); next} layout && /^\| `/' "$output/README.md" \
    | cut -d'|' -f2 | grep -o '`[^`]*`' | tr -d '`')"
  [[ -n "$layout_paths" ]]
  while IFS= read -r layout_path; do
    if [[ ! -e "$output/$layout_path" ]]; then
      echo "The generated README lists a missing project path: $layout_path" >&2
      exit 1
    fi
  done <<< "$layout_paths"
  phase typecheck
  if [[ "$framework" == vue && "$manager" == bun ]]; then
    # vue-tsc finds no .vue files under the Bun runtime and passes, so check
    # with Node as a plain `bun run` does.
    (cd "$output/Frontend" && bun run typecheck)
  else
    frontend_script "$manager" "$output/Frontend" typecheck
  fi
  # The frontend test runs the page against the generated typed mocks, which the build wrote.
  test -f "$output/Frontend/src/generated/workspace.mock.ts"
  phase frontend-test
  frontend_script "$manager" "$output/Frontend" test
  phase serve-release
  if [[ "$host" == cswebui ]]; then
    serve_and_fetch "$output" "$output/serve-release.log" "$output/release-document.html" false \
      dotnet run --project "$output/$project_name.csproj" --configuration Release --no-build
  fi
  phase dock-dev
  # #954: under dotnet runic dev the Counter button's command is a row of the Runic dock with its [RelayCommand]
  # method, which the row opens in the editor. Once per Vite frontend: its CS-WebUI CommunityToolkit.Mvvm variant.
  if [[ "$browser_check" == 1 && "$framework" != angular && "$manager" == npm && "$host" == cswebui && "$view_models" == toolkit ]]; then
    run_dock_dev_check "$output" "$framework" "$host"
  fi
  phase translations-dev
  # #944: under dotnet runic dev an edited, added or removed message reaches the page without a build.
  if [[ "$browser_check" == 1 && "$translations" == translations && "$framework" != angular ]]; then
    run_translations_dev_check "$output" "$framework" "$host"
  fi
  phase browser
  # Each frontend's page runs in Chromium under its policy on both hosts: the default Runic Desktop project and the
  # CS-WebUI variant, both with npm. The policy depends on the frontend alone, so the GTK 4 variant adds nothing.
  if [[ "$browser_check" == 1 && "$manager" == npm && "$linux_gtk" == 3 ]]; then
    run_browser_check "$output" "$project_name" "$framework" "$host"
  fi
  phase desktop-smoke
  # The smoke lane opens the default Runic Desktop (GTK 3) project once, with
  # npm, and every GTK 4 variant.
  if [[ "${RUNIC_TEMPLATE_DESKTOP_SMOKE:-0}" == 1 ]] &&
    [[ "$linux_gtk" == 4 || ( "$host" == desktop && "$manager" == npm ) ]]; then
    run_desktop_smoke "$output" "$project_name" "$host_label" "$linux_gtk"
  fi
  phase dotnet-test
  if [[ "$tests" == tests ]]; then
    # --translations replaces the welcome test with three: the greeting in English and German, and a regional or
    # unknown language.
    if [[ "$translations" == translations ]]; then run_generated_tests "$output" "$project_name" 5
    else run_generated_tests "$output" "$project_name" 3; fi
  fi
  phase_report "$framework|$manager|$host_label|$view_models|${tests:-no-tests}${restore:+|restore}${translations:+|translations}"
  printf 'TEMPLATE_OK|%s|%s|%s|%s|%s%s%s\n' "$framework" "$manager" "$host_label" "$view_models" "${tests:-no-tests}" \
    "${restore:+|restore}" "${translations:+|translations}"
}

# The RuntimeIdentifier of this machine, or nothing on a platform Runic does not target.
host_runtime_identifier() {
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) echo linux-x64 ;;
    Linux-aarch64) echo linux-arm64 ;;
    Darwin-arm64) echo osx-arm64 ;;
  esac
}

# What a package consumer's publish carries (fdd-size-0.22 §7.3): no XML documentation, no Bridge code generator and no
# source generator or compiler. Runic.Platform.Windows and Runic.Platform.MacOS come with the Runic Desktop host only,
# and a RID-specific publish keeps only its own operating system's (D072.9): the Linux providers and their Linux-only
# libraries leave a Windows or macOS publish, and the publish's deps.json lists no assembly it left out.
check_published_files() {
  local directory="$1"
  local host="$2"
  local rid="$3"
  local unexpected
  unexpected="$(cd "$directory" && find . -type f \( -name '*.xml' -o -name 'BridgeCodegen*' -o -name '*.Generator*.dll' \
    -o -name '*.Compiler*.dll' \) | sort)"
  if [[ -n "$unexpected" ]]; then
    echo "The $host publish ($rid) contains documentation or build tools:" >&2
    echo "$unexpected" >&2
    exit 1
  fi
  local name expected present
  for name in Windows MacOS; do
    expected=false
    if [[ "$host" == desktop* ]]; then
      case "$name:$rid" in
        *:portable | Windows:win-* | MacOS:osx-*) expected=true ;;
      esac
    fi
    present=false
    [[ -f "$directory/Runic.Platform.$name.dll" ]] && present=true
    if [[ "$present" != "$expected" ]]; then
      echo "The $host publish ($rid) $([[ "$present" == true ]] && echo contains || echo lacks) Runic.Platform.$name.dll." >&2
      exit 1
    fi
  done
  if [[ "$rid" == win-* || "$rid" == osx-* ]] &&
    find "$directory" -maxdepth 1 \( -name 'Runic.Platform.Linux*.dll' -o -name 'Tmds.DBus.Protocol.dll' \) | grep .; then
    echo "The $host publish ($rid) contains Linux-only assemblies." >&2
    exit 1
  fi
  local deps listed
  deps="$(find "$directory" -maxdepth 1 -name '*.deps.json' | head -n 1)"
  for listed in $(grep -oE '"[A-Za-z0-9.-]+\.dll"' "$deps" | tr -d '"' | sort -u); do
    if [[ "$listed" == Runic.* || "$listed" == Tmds.* ]] && [[ ! -f "$directory/$listed" ]]; then
      echo "The $host publish ($rid) lists $listed in $(basename "$deps") without the file." >&2
      exit 1
    fi
  done
}

# Runs the generated test project as its README documents. The tests build the
# application in Debug without its frontend, so no Debug frontend stamp appears.
run_generated_tests() {
  local output="$1"
  local project_name="$2"
  local expected_tests="$3"
  local log="$output/dotnet-test.log"
  if ! (cd "$output" && dotnet test --project "$project_name.Tests") > "$log" 2>&1; then
    echo "The generated tests failed." >&2
    tail -n 80 "$log" >&2
    exit 1
  fi
  if ! grep -Eq "succeeded: $expected_tests\b" "$log" || grep -Eq 'failed: [1-9]' "$log"; then
    echo "The generated test run did not pass its $expected_tests tests." >&2
    tail -n 40 "$log" >&2
    exit 1
  fi
  if [[ -e "$output/obj/Debug/net10.0/runic-bridge-frontend.stamp" ]]; then
    echo "Building the generated tests built the frontend." >&2
    exit 1
  fi
  # The test project uses the app's Bridges: TUnit sets IsTestProject and it declares
  # no Window or View, so it neither generates nor reports RUNICBRIDGE006 or 015.
  if [[ -e "$output/$project_name.Tests/obj/runic-bridge" || -e "$output/$project_name.Tests/obj/Debug/net10.0/runic-bridge" ]] \
    || grep -Eq 'RUNICBRIDGE[0-9]+' "$log"; then
    echo "Building the generated tests ran Bridge generation in the test project." >&2
    exit 1
  fi
  echo "TESTS_OK|$project_name"
}

# Serves the Release build as a production application does (RUNIC_APPLICATION_SERVE_ONLY, no DOTNET_ENVIRONMENT)
# and opens its page in Chromium, which must report no Content-Security-Policy violation while the Bridge connects
# and the Counter View increments. Runic Desktop sends the generated Program.cs policy as a header; CS-WebUI sends no
# headers. SvelteKit's kit.csp adds a <meta> policy on both hosts.
run_browser_check() {
  local output="$1"
  local project_name="$2"
  local framework="$3"
  local host="$4"
  local options=(--host "$host")
  [[ "$host" == desktop ]] && options+=(--policy-from "$output/Program.cs")
  [[ "$framework" == sveltekit ]] && options+=(--meta-policy)
  local log="$output/browser-check.log"
  if ! bun "$script_directory/check-template-page.mjs" "${options[@]}" -- \
    env -C "$output" dotnet "$output/bin/Release/net10.0/$project_name.dll" > "$log" 2>&1; then
    echo "The $framework $host page failed the browser check." >&2
    cat "$log" >&2
    exit 1
  fi
  # The check's result line, then how its application stopped (STOP|…).
  printf '%s|%s|%s\n' "$(grep -E '^[A-Z_]+_OK\|' "$log" | tail -n 1)" "$framework" "$host"
  grep -E '^STOP\|' "$log" | tail -n 1 || true
}

# Opens the page of a translated Vite frontend under dotnet runic dev in Chromium, edits a message of its catalog and
# waits for the page to show it, then adds and removes a message (check-template-translations-dev.mjs). The catalog's
# files are restored afterwards.
run_translations_dev_check() {
  local output="$1"
  local framework="$2"
  local host="$3"
  local log="$output/translations-dev-check.log"
  if ! bun "$script_directory/check-template-translations-dev.mjs" --host "$host" --project "$output" -- \
    env -C "$output" dotnet runic dev > "$log" 2>&1; then
    echo "The $framework $host page did not follow its catalog under dotnet runic dev." >&2
    cat "$log" >&2
    exit 1
  fi
  # The check's result line, then how its application stopped (STOP|…).
  printf '%s|%s|%s\n' "$(grep -E '^[A-Z_]+_OK\|' "$log" | tail -n 1)" "$framework" "$host"
  grep -E '^STOP\|' "$log" | tail -n 1 || true
}

# Opens the page of a CommunityToolkit.Mvvm Vite frontend under dotnet runic dev in Chromium, increments the Counter
# and checks the Runic dock's row for the command and its open button, and Runic Inspect's C# location for it and
# its link (check-template-dock.mjs).
run_dock_dev_check() {
  local output="$1"
  local framework="$2"
  local host="$3"
  local log="$output/dock-dev-check.log"
  if ! bun "$script_directory/check-template-dock.mjs" --host "$host" --project "$output" -- \
    env -C "$output" dotnet runic dev > "$log" 2>&1; then
    echo "The $framework $host page did not show its command in the Runic dock or Runic Inspect under dotnet runic dev." >&2
    cat "$log" >&2
    exit 1
  fi
  # The check's result line, then how its application stopped (STOP|…).
  printf '%s|%s|%s\n' "$(grep -E '^[A-Z_]+_OK\|' "$log" | tail -n 1)" "$framework" "$host"
  grep -E '^STOP\|' "$log" | tail -n 1 || true
}

# Runs the built Desktop application once under Xvfb with RUNIC_APPLICATION_CLOSE_AFTER_OPEN:
# the generated Program must open its embedded window, connect its bridge, and exit 0 without
# manual wiring. Needs Xvfb, dbus-run-session and WebKitGTK 4.1 (GTK 3) or GTK 4.12+ with
# WebKitGTK 6 (--linux-gtk 4).
run_desktop_smoke() {
  local output="$1"
  local project_name="$2"
  local host="$3"
  local linux_gtk="$4"
  local log="$output/desktop-smoke.log"
  local status=0
  # The application runs directly, not through dotnet run, so no build server keeps the session open.
  env -C "$output" RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1 GDK_BACKEND=x11 GSETTINGS_BACKEND=memory \
    timeout -k 10 180 dbus-run-session -- xvfb-run -a \
    dotnet "$output/bin/Release/net10.0/$project_name.dll" > "$log" 2>&1 < /dev/null || status=$?
  if [[ "$status" != 0 ]] || ! grep -Fq 'RUNIC_APPLICATION_OPENED=Embedded' "$log"; then
    echo "The $host application did not open its embedded window and exit (status $status)." >&2
    tail -n 80 "$log" >&2
    exit 1
  fi
  if [[ "$linux_gtk" == 4 ]]; then
    check_diagnostics_log "$output" "$project_name"
  fi
  echo "DESKTOP_SMOKE_OK|$project_name"
}

# IDE debugging on Linux (#1029, D066.39): VS Code's JavaScript debugger attached through dotnet runic dev's CDP adapter
# to a generated React project's WebKitGTK Window (check-webkit-cdp.mjs): source-mapped breakpoints, stepping, the
# console, reloads, a module that loads after 2,000 others, HMR and a Window restart. RUNIC_TEMPLATE_WEBKIT_CDP=only runs
# it alone, for GTK 3 and GTK 4 (the webkit-cdp CI job); it needs Xvfb, dbus-run-session and both WebKitGTK runtimes.
# Under Vite, js-debug's default perScriptSourcemaps soon stops at the start of each file it has breakpoints for
# instead of at the adapter's instrumentation pause before each script; GTK 3 runs with that turned off, for both.
verify_webkit_cdp() {
  local linux_gtk="$1"
  local per_script_sourcemaps="$2"
  local project_name="AcceptanceWebKitCdpGtk$linux_gtk"
  local output="$template_tmp/webkit-cdp-gtk$linux_gtk"
  local gtk_options=()
  [[ "$linux_gtk" == 4 ]] && gtk_options=(--linux-gtk 4)
  dotnet new runic-app --name "$project_name" --output "$output" --frontend react --package-manager npm --host desktop \
    "${gtk_options[@]}"
  configure_candidate_registry npm "$output"
  (cd "$output" && dotnet tool restore)
  local log="$output/webkit-cdp-check.log"
  if ! node "$script_directory/check-webkit-cdp.mjs" --project "$output" --per-script-sourcemaps "$per_script_sourcemaps" > "$log" 2>&1; then
    echo "js-debug did not debug the GTK $linux_gtk Window through dotnet runic dev's CDP adapter." >&2
    cat "$log" >&2
    exit 1
  fi
  grep -E '^(WEBKIT_CDP|\[[0-9.]+s\] (PASS|FAIL))' "$log" || true
  printf 'WEBKIT_CDP_TEMPLATE_OK|gtk%s\n' "$linux_gtk"
}

# dotnet runic dev --browser (#1031): the "Window in Chrome" launch configuration of a project generated with --editor
# vscode, run as VS Code runs it: js-debug launches Chrome at dev's entry address, which sends it to the serve-only
# Window, and the same scenarios run on Chromium (check-webkit-cdp.mjs --browser). Part of RUNIC_TEMPLATE_WEBKIT_CDP=only.
verify_dev_browser() {
  local project_name="AcceptanceDevBrowser"
  local output="$template_tmp/dev-browser"
  dotnet new runic-app --name "$project_name" --output "$output" --frontend react --package-manager npm --host desktop --editor vscode
  configure_candidate_registry npm "$output"
  (cd "$output" && dotnet tool restore)
  local log="$output/dev-browser-check.log"
  if ! node "$script_directory/check-webkit-cdp.mjs" --project "$output" --browser > "$log" 2>&1; then
    echo "js-debug did not debug the Window in Chrome through dotnet runic dev --browser." >&2
    cat "$log" >&2
    exit 1
  fi
  grep -E '^(DEV_BROWSER|\[[0-9.]+s\] (PASS|FAIL))' "$log" || true
  echo "DEV_BROWSER_TEMPLATE_OK"
}

# The generated DiagnosticsLog keeps the Desktop diagnostics that standard error loses outside a terminal. A copy of
# the built application whose page never loads the Runic client reports presentation-connection-timeout; the log in
# the user's data directory, named after the application ID, holds it once, and a second start replaces the log.
check_diagnostics_log() {
  local output="$1"
  local project_name="$2"
  local copy="$template_tmp/diagnostics-$project_name"
  local data="$copy-data"
  local application_id log run
  application_id="$(sed -n 's|.*<ApplicationId>\(.*\)</ApplicationId>.*|\1|p' "$output/$project_name.csproj")"
  log="$data/$application_id/diagnostics.log"
  rm -rf "$copy" "$data"
  cp -R "$output/bin/Release/net10.0" "$copy"
  printf '<!doctype html><title>No Runic client</title>\n' > "$copy/www/index.html"
  for run in 1 2; do
    env -C "$copy" XDG_DATA_HOME="$data" GDK_BACKEND=x11 GSETTINGS_BACKEND=memory \
      timeout -k 10 120 dbus-run-session -- xvfb-run -a \
      dotnet "$copy/$project_name.dll" > "$copy/stderr-$run.log" 2>&1 < /dev/null || true
    if ! grep -Fq 'presentation-connection-timeout' "$copy/stderr-$run.log"; then
      echo "The $project_name copy without a Runic client reported no presentation-connection-timeout (run $run)." >&2
      tail -n 40 "$copy/stderr-$run.log" >&2
      exit 1
    fi
  done
  if [[ "$(grep -c 'presentation-connection-timeout' "$log" 2> /dev/null)" != 1 ]]; then
    echo "$log does not hold exactly the last start's diagnostic." >&2
    cat "$log" >&2 || true
    exit 1
  fi
  rm -rf "$copy" "$data"
  echo "DIAGNOSTICS_LOG_OK|$project_name"
}

# The guided creator must produce exactly what its printed dotnet new command
# produces, using the packaged tool and the candidate template.
verify_creator() {
  local creator_directory="$template_tmp/creator"
  # --application-id is a text option that the creator generates from the template model.
  local options=(--frontend vue --package-manager pnpm --host cswebui --view-models toolkit --tests --application-id org.example.AcceptanceCreator)
  dotnet tool install Runic.Create \
    --tool-path "$tool_directory" \
    --version "$package_version" \
    "${tool_source_options[@]}" \
    "${tool_restore_options[@]}"
  mkdir -p "$creator_directory/guided" "$creator_directory/direct"
  (cd "$creator_directory/guided" && "$tool_directory/runic-create" AcceptanceCreator "${options[@]}" \
    --template-source "$package_directory" --dry-run) > "$creator_directory/plan.txt"
  grep -Fq "dotnet new runic-app --name AcceptanceCreator ${options[*]}" "$creator_directory/plan.txt"
  grep -Fq "dnx Runic.Create@$package_version -- AcceptanceCreator ${options[*]}" "$creator_directory/plan.txt"
  test ! -e "$creator_directory/guided/AcceptanceCreator"
  (cd "$creator_directory/guided" && "$tool_directory/runic-create" AcceptanceCreator "${options[@]}" \
    --template-source "$package_directory") > "$creator_directory/create.txt"
  (cd "$creator_directory/direct" && dotnet new runic-app --name AcceptanceCreator "${options[@]}")
  diff -r "$creator_directory/guided/AcceptanceCreator" "$creator_directory/direct/AcceptanceCreator"
  grep -Fq 'dotnet runic dev' "$creator_directory/create.txt"
  if (cd "$creator_directory/guided" && "$tool_directory/runic-create" Other --frontend qt --yes --dry-run) \
    > "$creator_directory/invalid.txt" 2>&1; then
    echo "The creator accepted an unknown frontend." >&2
    exit 1
  fi
  grep -Fq 'react, vue, svelte, sveltekit, angular' "$creator_directory/invalid.txt"
  grep -Fq '<ApplicationId>org.example.AcceptanceCreator</ApplicationId>' "$creator_directory/guided/AcceptanceCreator/AcceptanceCreator.csproj"
  # --no-install uses the template installed above and prints only the dotnet new command.
  (cd "$creator_directory/guided" && "$tool_directory/runic-create" Offline --no-install --yes --dry-run) \
    > "$creator_directory/no-install.txt"
  if grep -Fq 'dotnet new install' "$creator_directory/no-install.txt"; then
    echo "The creator installs the template with --no-install." >&2
    exit 1
  fi
  # Accepting every default selects Runic Desktop on the native server (D053.24) and ReactiveUI.
  (cd "$creator_directory/guided" && "$tool_directory/runic-create" Defaults --yes --dry-run) \
    > "$creator_directory/defaults.txt"
  grep -Fq 'dotnet new runic-app --name Defaults --frontend react --package-manager npm --host desktop --linux-gtk 3 --desktop-server native --view-models reactiveui' \
    "$creator_directory/defaults.txt"
  echo 'CREATOR_OK'
}

# Project names that are not npm names still produce a valid frontend package name.
verify_package_names() {
  local names_directory="$template_tmp/package-names"
  local case_entry project_name expected generated index=0
  local long_name
  long_name="$(printf 'a%.0s' {1..220})"
  mkdir -p "$names_directory"
  # Names without npm characters fall back to "app"; npm names have at most 214 characters.
  for case_entry in "Contoso.Notes App|contoso-notes-app" "_My__API.|my-api" "Café Notes|caf-notes" \
    "日本語|app" "___|app" "$long_name|${long_name:0:214}"; do
    project_name="${case_entry%%|*}"
    expected="${case_entry#*|}"
    index=$((index + 1))
    (cd "$names_directory" && dotnet new runic-app --name "$project_name" --output "case-$index" --frontend svelte)
    generated="$(bun -e 'process.stdout.write(require(process.argv[1]).name)' "$names_directory/case-$index/Frontend/package.json")"
    if [[ "$generated" != "$expected" ]]; then
      echo "Project '$project_name' produced frontend package name '$generated', expected '$expected'." >&2
      exit 1
    fi
  done
  # The application ID: com.example. and the name's ASCII letters and digits, starting with a letter, at most 50
  # characters (an MSIX package name), or the --application-id given.
  index=0
  for case_entry in "Contoso.Notes App|com.example.ContosoNotesApp" "_My__API.|com.example.MyAPI" \
    "Café Notes|com.example.CafNotes" "日本語|com.example.App" "2048 Game|com.example.App2048Game" \
    "$long_name|com.example.${long_name:0:38}"; do
    project_name="${case_entry%%|*}"
    expected="${case_entry#*|}"
    index=$((index + 1))
    (cd "$names_directory" && dotnet new runic-app --name "$project_name" --output "id-$index" --frontend svelte) > /dev/null
    check_application_id "$names_directory/id-$index" "$project_name" "$expected"
  done
  (cd "$names_directory" && dotnet new runic-app --name Notes --output id-explicit --application-id org.example.Notes --nix) > /dev/null
  check_application_id "$names_directory/id-explicit" Notes org.example.Notes
  grep -Fq 'name = "org.example.Notes";' "$names_directory/id-explicit/flake.nix"
  grep -Fq 'iconName = "org.example.Notes";' "$names_directory/id-explicit/flake.nix"
  echo 'PACKAGE_NAMES_OK'
  echo 'APPLICATION_IDS_OK'
  # --packaging adds packaging/ unchanged (dotnet runic package embeds the same manifests, which the packaging CI job
  # runs) and the properties it reads; the command replaced package.mjs (D039.14).
  local packaging_source="$repository_root/tools/Runic.Application.Templates/content/runic-app/packaging"
  if [[ -e "$names_directory/id-explicit/packaging" ]]; then
    echo "A project without --packaging has packaging/." >&2
    exit 1
  fi
  (cd "$names_directory" && dotnet new runic-app --name Packaged --output packaged --host desktop --linux-gtk 4 --packaging) > /dev/null
  if ! diff -r "$packaging_source" "$names_directory/packaged/packaging"; then
    echo "--packaging did not copy packaging/ unchanged." >&2
    exit 1
  fi
  for property in Description PackageProjectUrl RunicApplicationPublisher RunicApplicationUrlSchemes RunicApplicationFileTypes RunicApplicationWebLinkHosts RunicApplicationDBusActivatable; do
    grep -Fq "<$property>" "$names_directory/packaged/Packaged.csproj"
  done
  grep -Fxq '/artifacts/' "$names_directory/packaged/.gitignore"
  if [[ -e "$names_directory/packaged/packaging/package.mjs" ]]; then
    echo "--packaging still adds package.mjs, which dotnet runic package replaced." >&2
    exit 1
  fi
  echo 'PACKAGING_RECIPES_OK'
  # sdk#1386: --library does not support --translations or --nix yet. dotnet new creates the solution without them and
  # warns, naming each option it left out.
  local warned="$names_directory/library-unsupported.txt"
  (cd "$names_directory" && dotnet new runic-app --name Shared --output library-unsupported --library Shared.Core \
    --translations --nix) > "$warned"
  grep -Fq 'Warning: --translations was left out. --library does not support Translations yet' "$warned"
  grep -Fq 'Warning: --nix was left out. --library does not support the Nix flake yet' "$warned"
  if [[ -e "$names_directory/library-unsupported/flake.nix" || -e "$names_directory/library-unsupported/Shared/Frontend/translations" ]] \
    || grep -q 'Runic\.Translations' "$names_directory/library-unsupported/Shared/Shared.csproj"; then
    echo "--library kept --translations or --nix." >&2
    exit 1
  fi
  echo 'LIBRARY_UNSUPPORTED_OPTIONS_WARNED_OK'
}

# The project file declares the application ID once, and the icons are copied unchanged.
check_application_id() {
  local output="$1"
  local project_name="$2"
  local expected="$3"
  local generated
  generated="$(sed -n 's|.*<ApplicationId>\(.*\)</ApplicationId>.*|\1|p' "$output/$project_name.csproj")"
  if [[ "$generated" != "$expected" ]]; then
    echo "Project '$project_name' declared application ID '$generated', expected '$expected'." >&2
    exit 1
  fi
  for icon in icon.svg icon.png; do
    if ! cmp -s "$repository_root/tools/Runic.Application.Templates/content/runic-app/icons/$icon" "$output/icons/$icon"; then
      echo "Project '$project_name' did not get icons/$icon unchanged." >&2
      exit 1
    fi
  done
  # The build derives the Windows icon from icon.png (sdk#1207); the project has no .ico of its own.
  if [[ -e "$output/icons/icon.ico" ]] || grep -q '<ApplicationIcon>' "$output/$project_name.csproj"; then
    echo "Project '$project_name' still has a hand-made icons/icon.ico or an ApplicationIcon." >&2
    exit 1
  fi
}

# --library <Name> (D081.5): a solution of the application, a client library with a View and its frontend package, and
# one JavaScript workspace at the root with the lock for the selected manager. It is checked as a fresh clone, with the
# documented commands only and no manual step: dotnet build of the solution installs the workspace once at its root,
# builds the library's package and then the application's page, which imports the package; the frontends' type checks
# and tests pass; dotnet runic dev, from a clean checkout again, serves the page, whose module resolves the built package
# (CS-WebUI variants, whose served documents the checks fetch). The flags are the trailing ones of verify_template.
verify_library_template() {
  local framework="$1"
  local manager="$2"
  local host="$3"
  local view_models="$4"
  local tests="" flag
  for flag in "${@:5}"; do
    case "$flag" in
      tests) tests=tests ;;
      *) echo "Unknown verify_library_template flag: $flag" >&2; exit 2 ;;
    esac
  done
  local frontend_options=(--frontend "$framework") framework_name="${framework^}"
  if [[ "$framework" == angular-vite ]]; then frontend_options=(--frontend angular --angular-build vite); framework_name=AngularVite; fi
  local project_name="AcceptanceLibrary${framework_name}${manager^}${host^}"
  local library_name="$project_name.Core"
  local output="$template_tmp/library-$framework-$manager-$host-$view_models"
  local app="$output/$project_name" library="$output/$library_name"
  # The checks' logs stay outside the clone, whose git status must show no file that a build leaves.
  local logs="$output.logs"
  mkdir -p "$logs"
  local selected_lock
  case "$manager" in
    npm) selected_lock=package-lock.json ;;
    pnpm) selected_lock=pnpm-lock.yaml ;;
    bun) selected_lock=bun.lock ;;
  esac
  local tests_options=()
  [[ "$tests" == tests ]] && tests_options=(--tests)

  phase generate
  dotnet new runic-app --name "$project_name" --output "$output" "${frontend_options[@]}" --package-manager "$manager" \
    --host "$host" --view-models "$view_models" --editor vscode --library "$library_name" "${tests_options[@]}"

  # The solution lists the application first, so Visual Studio starts it.
  local first_project
  first_project="$(grep -o '<Project Path="[^"]*"' "$output/$project_name.slnx" | head -n 1)"
  [[ "$first_project" == "<Project Path=\"$project_name/$project_name.csproj\"" ]]
  grep -Fq "<Project Path=\"$library_name/$library_name.csproj\" />" "$output/$project_name.slnx"
  if [[ "$tests" == tests ]]; then
    grep -Fq "<Project Path=\"$project_name.Tests/$project_name.Tests.csproj\" />" "$output/$project_name.slnx"
    # #1424: the reference passes no properties, so the solution build builds the application once, not a second time
    # with other properties at the same time and into the same folders.
    grep -Fq "<ProjectReference Include=\"../$project_name/$project_name.csproj\" />" "$output/$project_name.Tests/$project_name.Tests.csproj"
    test -f "$output/global.json"
  fi
  # The solution's folder has what every project shares; the application's folder has only the application.
  local shared
  for shared in .config/dotnet-tools.json .gitignore .vsconfig .vscode/launch.json .vscode/tasks.json package.json README.md \
    "$selected_lock"; do
    test -f "$output/$shared"
  done
  for shared in .config .gitignore .vsconfig .vscode global.json package-lock.json pnpm-lock.yaml bun.lock "$project_name.Tests"; do
    if [[ -e "$app/$shared" ]]; then
      echo "The library variant's application folder has $shared, which belongs to the solution's folder." >&2
      exit 1
    fi
  done
  # One lock, the workspace root's: the frontends have none of their own.
  local other_lock
  for other_lock in package-lock.json pnpm-lock.yaml bun.lock; do
    [[ "$other_lock" == "$selected_lock" ]] || test ! -e "$output/$other_lock"
    test ! -e "$app/Frontend/$other_lock"
    test ! -e "$library/Frontend/$other_lock"
  done
  [[ "$manager" != pnpm ]] || grep -Fxq "  - $library_name/Frontend" "$output/pnpm-workspace.yaml"
  [[ "$manager" == pnpm ]] || test ! -e "$output/pnpm-workspace.yaml"
  test ! -e "$app/Frontend/pnpm-workspace.yaml"
  # --library leaves out what it does not combine with yet.
  test ! -e "$output/flake.nix"
  test ! -e "$app/flake.nix"
  # The workspace: both frontends, the package manager, and the application frontend's dependency on the library's package.
  local library_package app_package
  library_package="$(bun -e 'process.stdout.write(require(process.argv[1]).name)' "$library/Frontend/package.json")"
  app_package="$(bun -e 'process.stdout.write(require(process.argv[1]).name)' "$app/Frontend/package.json")"
  [[ -n "$library_package" && "$library_package" != "$app_package" ]]
  bun -e '
    const [root, app, library, manager, dependency] = process.argv.slice(1).map((value, index) => index < 3 ? require(value) : value);
    const fail = message => { console.error(message); process.exit(1); };
    if (JSON.stringify(root.workspaces) !== JSON.stringify([process.argv[6], process.argv[7]])) fail(`workspaces: ${root.workspaces}`);
    for (const manifest of [root, app, library]) if (!manifest.packageManager?.startsWith(`${manager}@`)) fail(`packageManager: ${manifest.packageManager}`);
    if (app.dependencies[library.name] !== dependency) fail(`the application frontend depends on ${library.name} ${app.dependencies[library.name]}`);
    if (!library.scripts?.build) fail("the library package has no build script");
  ' "$output/package.json" "$app/Frontend/package.json" "$library/Frontend/package.json" "$manager" \
    "$([[ "$manager" == npm ]] && echo '*' || echo 'workspace:*')" "$library_name/Frontend" "$project_name/Frontend"
  # The projects: the library is a client library that declares a View, the application references and maps it.
  grep -Fq '<RunicApplicationFrontendClientLibrary>true</RunicApplicationFrontendClientLibrary>' "$library/$library_name.csproj"
  grep -Fq "public sealed partial class AboutView : " "$library/Views.cs"
  grep -Fq "namespace $library_name;" "$library/AboutViewModel.cs"
  grep -Fq "<ProjectReference Include=\"../$library_name/$library_name.csproj\" />" "$app/$project_name.csproj"
  grep -Fq "<RunicApplicationFrontendClientModule Include=\"$library_name\" Specifier=\"$library_package/generated\" />" "$app/$project_name.csproj"
  grep -Fq 'services.AddScoped<AboutViewModel>();' "$app/WorkspaceServices.cs"
  grep -Fq 'public AboutViewModel About { get; }' "$app/WorkspaceViewModel.cs"
  if grep -vE 'RunicApplicationFrontend(WindowProject|ClientModule)\b' "$app/$project_name.csproj" | grep -Eq 'RunicApplicationFrontend|RunicBridge'; then
    echo "The library variant's application declares settings the Runic packages default." >&2
    exit 1
  fi
  if grep -vE 'RunicApplicationFrontendClientLibrary>' "$library/$library_name.csproj" | grep -Eq 'RunicApplicationFrontend|RunicBridge'; then
    echo "The library variant's library declares settings the Runic packages default." >&2
    exit 1
  fi
  # VS Code opens the solution's folder: its paths lead into the application's (F10).
  grep -Fq "\"program\": \"\${workspaceFolder}/$project_name/bin/Debug/net10.0/$project_name\"" "$output/.vscode/launch.json"
  grep -Fq "\"webRoot\": \"\${workspaceFolder}/$project_name/Frontend\"" "$output/.vscode/launch.json"
  grep -Fq "\"args\": [\"runic\", \"dev\", \"--project\", \"$project_name/$project_name.csproj\"]," "$output/.vscode/tasks.json"
  if grep -nE '"\$\{workspaceFolder\}/(bin|obj|Frontend)/' "$output/.vscode/launch.json"; then
    echo "The library variant's launch.json names a path below the solution's folder instead of the application's." >&2
    exit 1
  fi
  if grep -rnIE --exclude='*lock*' '^[[:space:]]*(#|//#)(if|elif|else|endif)\b|<!--#|__[A-Z][A-Z_]+__|RunicCoreLibrary|RunicCoreNamespace|runic-library-starter|runic-app-starter' "$output"; then
    echo "The generated library variant contains unprocessed template syntax." >&2
    exit 1
  fi
  # The candidate registry for the workspace, whose root installs both frontends.
  (cd "$output" && npm config set --location=project @runic-artifex:registry "$registry_url" \
    && npm config set --location=project fetch-timeout 60000)
  RUNIC_TEMPLATE_NPM_REGISTRY="$registry_url" bun "$script_directory/bind-template-candidate-integrities.mjs" \
    "$output/$selected_lock" "${npm_archives[@]}" "${train_npm_archives[@]}"
  # A fresh clone: the committed solution, without any build output.
  git -C "$output" init -q
  git -C "$output" add -A
  git -C "$output" -c user.name=Acceptance -c user.email=acceptance@example.invalid commit -qm 'Generated solution'

  phase build
  (cd "$output" && dotnet tool restore)
  # The first build of the solution: the workspace install, the library's clients and package, then the application.
  if ! dotnet build "$output/$project_name.slnx" --configuration Release > "$logs/build.log" 2>&1; then
    echo "The library variant's solution did not build on a fresh clone." >&2
    tail -n 80 "$logs/build.log" >&2
    exit 1
  fi
  test -d "$output/node_modules"
  test -f "$library/Frontend/src/generated/runic-clients.ts"
  test -f "$library/Frontend/dist/index.js"
  test -f "$library/Frontend/dist/generated/runic-clients.js"
  # The compiled package carries the contract declarations its typed clients import.
  test -f "$library/Frontend/dist/generated/about.contract.d.ts"
  # The application imports the library's clients instead of generating copies, and serves no page of the library.
  test -f "$app/Frontend/src/generated/workspace.ts"
  test -f "$app/Frontend/src/generated/runic-client-modules.ts"
  test ! -e "$app/Frontend/src/generated/about.ts"
  grep -Fq "from \"$library_package/generated\"" "$app/Frontend/src/generated/workspace.ts"
  test -f "$app/bin/Release/net10.0/www/index.html"
  grep -rFq 'Show details' "$app/bin/Release/net10.0/www"
  test ! -e "$library/bin/Release/net10.0/www"
  # .gitignore keeps every build output out of the clone; only the restore's NuGet lock files are new, to commit.
  local changed
  changed="$(git -C "$output" status --porcelain | grep -vE '^\?\? ([^/]+/)?packages\.lock\.json$' || true)"
  if [[ -n "$changed" ]]; then
    echo "The library variant's build left files that .gitignore does not cover:" >&2
    echo "$changed" >&2
    exit 1
  fi
  # Every path in the READMEs' layouts exists once the solution is built: the solution's and the application's.
  local readme base layout_paths layout_path
  for readme in "$output/README.md" "$app/README.md"; do
    base="$(dirname "$readme")"
    layout_paths="$(awk '/^## /{layout = ($0 == "## Solution layout" || $0 == "## Project layout"); next} layout && /^\| `/' "$readme" \
      | cut -d'|' -f2 | grep -o '`[^`]*`' | tr -d '`')"
    [[ -n "$layout_paths" ]]
    while IFS= read -r layout_path; do
      if [[ ! -e "$base/$layout_path" ]]; then
        echo "$readme lists a missing path: $layout_path" >&2
        exit 1
      fi
    done <<< "$layout_paths"
  done

  phase typecheck
  frontend_script "$manager" "$library/Frontend" typecheck
  if [[ "$framework" == vue && "$manager" == bun ]]; then
    (cd "$app/Frontend" && bun run typecheck)
  else
    frontend_script "$manager" "$app/Frontend" typecheck
  fi
  phase frontend-test
  # The page's test runs the library's about panel against the mocks the library's package exports.
  frontend_script "$manager" "$app/Frontend" test

  phase dev
  # dotnet runic dev from a clean checkout again, without a build before it, in the solution's folder: it takes the
  # solution's one Window project, the application.
  if [[ "$host" == cswebui ]]; then
    git -C "$output" clean -fdXq
    test ! -e "$library/Frontend/dist"
    local served_module="" served_module_expect=""
    case "$framework" in
      react) served_module=src/AboutPanel.tsx ;;
      vue) served_module=src/AboutPanel.vue ;;
      svelte) served_module=src/AboutPanel.svelte ;;
      sveltekit) served_module=src/lib/AboutPanel.svelte ;;
      angular-vite) served_module=src/app/about.ts ;;
    esac
    served_module_expect="$library_name/Frontend/dist/index.js"
    serve_and_fetch "$output" "$logs/dotnet-runic-dev.log" "$logs/dev-document.html" true \
      dotnet runic dev
    test -f "$library/Frontend/dist/index.js"
  fi

  phase dotnet-test
  if [[ "$tests" == tests ]]; then run_generated_tests "$output" "$project_name" 3; fi
  phase_report "library|$framework|$manager|$host|$view_models|${tests:-no-tests}"
  printf 'TEMPLATE_LIBRARY_OK|%s|%s|%s|%s|%s\n' "$framework" "$manager" "$host" "$view_models" "${tests:-no-tests}"
}

# angular-vite is --frontend angular --angular-build vite.
frameworks=(react vue svelte sveltekit angular angular-vite)
managers=(npm pnpm bun)
# Every frontend and package manager uses the default host and ViewModels,
# Runic Desktop and ReactiveUI, created without --host and --view-models.
# Pairwise variants then cover each frontend with CS-WebUI and npm, whose
# served documents the checks fetch, each frontend with CommunityToolkit.Mvvm,
# and every host and ViewModel pairing. Variants and the npm projects add the
# test project and run it, so the tests pass with every host and ViewModel
# library. SvelteKit adds a variant with the opt-in restore post-action.
variants=(
  "react npm cswebui toolkit tests"
  "vue npm cswebui reactiveui tests"
  "svelte npm cswebui toolkit tests"
  "angular npm cswebui reactiveui tests"
  "svelte bun desktop toolkit tests"
  "angular pnpm desktop toolkit tests"
  "vue npm desktop toolkit tests gtk4"
  "sveltekit npm cswebui toolkit tests restore"
  "sveltekit bun desktop toolkit tests"
  # --translations: the catalog's C# API and page modules from one runic.json (the Translations Vite plugin for the
  # Vite frontends, the .NET build's ESM for Angular), each frontend's language switch and its translated-text tests,
  # .NET and page, and the Vite frontends' development loop.
  "react pnpm desktop toolkit tests translations"
  "vue bun cswebui reactiveui tests translations"
  "svelte npm desktop reactiveui tests translations"
  "sveltekit npm desktop reactiveui tests translations"
  "angular npm desktop reactiveui tests translations"
  # --angular-build vite: the Vite build's development server on CS-WebUI, and its Translations development loop.
  "angular-vite npm cswebui toolkit tests"
  "angular-vite npm desktop reactiveui tests translations"
)
# --library (verify_library_template): one solution per frontend, so that each framework lane runs one, and each package
# manager twice. The CS-WebUI variants also run dotnet runic dev from a clean checkout; React with npm, the defaults, adds
# the test project.
library_variants=(
  "react npm cswebui reactiveui tests"
  "vue pnpm desktop toolkit"
  "svelte bun cswebui toolkit"
  "sveltekit npm desktop reactiveui"
  "angular pnpm cswebui reactiveui"
  "angular-vite bun cswebui toolkit"
)
# Keep CI's default matrix complete while allowing focused local debugging.
if [[ -n "${RUNIC_TEMPLATE_FRAMEWORKS:-}" ]]; then
  read -r -a frameworks <<< "$RUNIC_TEMPLATE_FRAMEWORKS"
fi
if [[ -n "${RUNIC_TEMPLATE_MANAGERS+set}" ]]; then
  # Space-separated managers of the default matrix; empty skips it, for a run of variants alone.
  read -r -a managers <<< "$RUNIC_TEMPLATE_MANAGERS"
fi
if [[ -n "${RUNIC_TEMPLATE_VARIANTS+set}" ]]; then
  # Semicolon-separated "frontend manager host view-models [tests]" entries; empty skips them.
  IFS=';' read -r -a variants <<< "$RUNIC_TEMPLATE_VARIANTS"
else
  # Default variants follow the selected frontends, so per-framework CI lanes
  # together run each variant exactly once.
  selected_variants=()
  for variant in "${variants[@]}"; do
    if [[ " ${frameworks[*]} " == *" ${variant%% *} "* ]]; then selected_variants+=("$variant"); fi
  done
  variants=("${selected_variants[@]}")
fi
if [[ -n "${RUNIC_TEMPLATE_LIBRARY_VARIANTS+set}" ]]; then
  # Semicolon-separated "frontend manager host view-models [tests]" entries; empty skips them.
  IFS=';' read -r -a library_variants <<< "$RUNIC_TEMPLATE_LIBRARY_VARIANTS"
else
  selected_variants=()
  for variant in "${library_variants[@]}"; do
    if [[ " ${frameworks[*]} " == *" ${variant%% *} "* ]]; then selected_variants+=("$variant"); fi
  done
  library_variants=("${selected_variants[@]}")
fi
for framework in "${frameworks[@]}"; do
  case "$framework" in
    react|vue|svelte|sveltekit|angular|angular-vite) ;;
    *) echo "Unknown template framework: $framework" >&2; exit 2 ;;
  esac
done
for manager in "${managers[@]}"; do
  case "$manager" in
    npm|pnpm|bun) ;;
    *) echo "Unknown template package manager: $manager" >&2; exit 2 ;;
  esac
done

case "${RUNIC_TEMPLATE_WEBKIT_CDP:-0}" in
  only)
    verify_webkit_cdp 3 no
    verify_webkit_cdp 4 auto
    verify_dev_browser
    exit 0
    ;;
  0) ;;
  *) echo "RUNIC_TEMPLATE_WEBKIT_CDP must be 0 or only." >&2; exit 2 ;;
esac

# CI runs the creator and package name checks in one framework lane only.
case "${RUNIC_TEMPLATE_CREATOR:-1}" in
  1) verify_creator; verify_package_names ;;
  0) ;;
  *) echo "RUNIC_TEMPLATE_CREATOR must be 0 or 1." >&2; exit 2 ;;
esac
for manager in "${managers[@]}"; do
  for framework in "${frameworks[@]}"; do
    if [[ "$manager" == npm ]]; then
      verify_template "$framework" "$manager" desktop reactiveui defaults tests
    else
      verify_template "$framework" "$manager" desktop reactiveui defaults
    fi
  done
done
for variant in "${variants[@]}"; do
  read -r -a fields <<< "$variant"
  verify_template "${fields[@]}"
done
for variant in "${library_variants[@]}"; do
  read -r -a fields <<< "$variant"
  verify_library_template "${fields[@]}"
done
