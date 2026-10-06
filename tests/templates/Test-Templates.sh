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
template_package="$package_directory/Runic.Application.Templates.$package_version.nupkg"
template_tmp="$(mktemp -d /tmp/runic-views-templates.XXXXXXXXXX)"
registry_pid=""
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
    echo '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
    echo '  </packageSources>'
    echo '</configuration>'
  } > "$template_tmp/NuGet.config"
fi

dotnet new install "$template_package" --force
tool_directory="$template_tmp/tools"
dotnet tool install dotnet-runic \
  --tool-path "$tool_directory" \
  --version "$package_version" \
  "${tool_source_options[@]}" \
  "${tool_restore_options[@]}"

pnpm_version="$(bun "$repository_root/eng/toolchain.mjs" pnpm)"
bun_version="$(bun "$repository_root/eng/toolchain.mjs" bun)"
package_manager_directory="$template_tmp/package-managers"
npm install --global --prefix "$package_manager_directory" "pnpm@$pnpm_version" \
  --allow-scripts=pnpm --no-audit --no-fund
export PATH="$package_manager_directory/bin:$PATH"
[[ "$(cd "$template_tmp" && pnpm --version)" == "$pnpm_version" ]]
[[ "$(bun --version)" == "$bun_version" ]]

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
bun "$script_directory/template-npm-registry.mjs" \
  "$registry_ready" "${npm_archives[@]}" &
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
  if [[ "$manager" == npm ]]; then
    RUNIC_TEMPLATE_NPM_REGISTRY="$registry_url" bun "$script_directory/bind-template-candidate-integrities.mjs" \
      "$output/Frontend/package-lock.json" "${npm_archives[@]}"
  fi
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
  setsid env -C "$directory" RUNIC_APPLICATION_SERVE_ONLY=1 "$@" > "$log" 2>&1 < /dev/null &
  served_group=$!
  local url=""
  for _ in $(seq 1 600); do
    url="$(grep -o 'RUNIC_APPLICATION_URL=[^[:space:]]*' "$log" | head -n 1 | cut -d= -f2- || true)"
    [[ -n "$url" ]] && break
    if ! kill -0 "$served_group" 2>/dev/null; then break; fi
    sleep 0.5
  done
  if [[ -z "$url" ]]; then
    echo "The application did not report its URL: $*" >&2
    tail -n 80 "$log" >&2
    exit 1
  fi
  # WebUI admits one client and identifies it by the cookie set on the first
  # response, so later requests must send it back.
  local cookies="$document.cookies"
  curl -fsS -c "$cookies" -b "$cookies" "$url/index.html" > "$document"
  curl -fsS -c "$cookies" -b "$cookies" "$url/runic-cswebui.js" > /dev/null
  if [[ "$expect_development" == true ]]; then
    grep -Fq 'http://127.0.0.1:' "$document"
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
    if grep -Fq 'http://127.0.0.1:' "$document"; then
      echo "The production build served a development document." >&2
      exit 1
    fi
  fi
  stop_served
}

verify_template() {
  local framework="$1"
  local manager="$2"
  local host="$3"
  local view_models="$4"
  local project_name="Acceptance${framework^}${manager^}${host^}${view_models^}"
  local output="$template_tmp/$framework-$manager-$host-$view_models"
  local expected_manager_version
  local selected_lock
  case "$manager" in
    npm) expected_manager_version="$(npm --version)"; selected_lock=package-lock.json ;;
    pnpm) expected_manager_version="$pnpm_version"; selected_lock=pnpm-lock.yaml ;;
    bun) expected_manager_version="$bun_version"; selected_lock=bun.lock ;;
  esac

  dotnet new runic-app \
    --name "$project_name" \
    --output "$output" \
    --frontend "$framework" \
    --package-manager "$manager" \
    --host "$host" \
    --view-models "$view_models" \
    --runic-version "$package_version" \
    --runic-npm-version "$views_npm_version"

  test -f "$output/Frontend/$selected_lock"
  for other_lock in package-lock.json pnpm-lock.yaml bun.lock; do
    [[ "$other_lock" == "$selected_lock" ]] || test ! -f "$output/Frontend/$other_lock"
  done
  grep -Fq "\"packageManager\": \"$manager@$expected_manager_version\"" "$output/Frontend/package.json"
  # The frontend package is named after the project, not the template.
  local package_name
  package_name="$(sed -E 's/([a-z0-9])([A-Z])/\1-\2/g' <<< "$project_name" | tr '[:upper:]' '[:lower:]')"
  grep -Fq "\"name\": \"$package_name\"" "$output/Frontend/package.json"
  grep -Fq 'RunicViewsWindowProject>true' "$output/$project_name.csproj"
  local index_html="$output/Frontend/index.html"
  [[ "$framework" == angular ]] && index_html="$output/Frontend/src/index.html"
  case "$host" in
    cswebui)
      grep -Fq 'Runic.Application.CsWebUi' "$output/$project_name.csproj"
      grep -Fq 'runic-cswebui.js' "$index_html"
      [[ "$framework" == angular ]] || grep -Fq 'runic()' "$output/Frontend/vite.config.ts"
      ;;
    desktop)
      grep -Fq 'Runic.Application.Desktop' "$output/$project_name.csproj"
      grep -Fq 'runic-desktop-views.js' "$index_html"
      [[ "$framework" == angular ]] || grep -Fq 'runic({ desktop: true })' "$output/Frontend/vite.config.ts"
      ;;
  esac
  case "$view_models" in
    toolkit) grep -Fq 'CommunityToolkit.Mvvm' "$output/$project_name.csproj" ;;
    reactiveui) grep -Fq 'Runic.Application.ReactiveUI' "$output/$project_name.csproj" ;;
  esac
  if grep -rnIE --exclude='*lock*' '^[[:space:]]*#(if|elif|else|endif)\b|<!--#|__[A-Z][A-Z_]+__' "$output"; then
    echo "The generated $framework project contains unprocessed template syntax." >&2
    exit 1
  fi
  if grep -Eq 'RunicBridgeBootstrap|RunicApplicationFrontend|RunicBridgeComposition' "$output/$project_name.csproj"; then
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

  # The documented first run, verbatim: dotnet tool restore, then
  # dotnet runic dev. doctor must already work before anything is restored.
  (cd "$output" && dotnet tool restore)
  if ! (cd "$output" && dotnet runic doctor) > "$output/doctor-unrestored.txt"; then
    cat "$output/doctor-unrestored.txt" >&2
    exit 1
  fi
  grep -Fq 'WARN compatibility-set: The project has not been restored yet' "$output/doctor-unrestored.txt"
  # RUNIC_APPLICATION_SERVE_ONLY is a CS-WebUI host contract. Desktop variants
  # open a native window, so automation covers their generation, build, and types.
  if [[ "$manager" == npm && "$host" == cswebui ]]; then
    serve_and_fetch "$output" "$output/dotnet-runic-dev.log" "$output/dev-document.html" true \
      dotnet runic dev
  fi

  dotnet restore "$output/$project_name.csproj" "${restore_sources[@]}"
  "$tool_directory/dotnet-runic" dev \
    --project "$output/$project_name.csproj" \
    --dry-run > "$output/dotnet-runic-dev-plan.txt"
  grep -Fq 'Runic Views Window project' "$output/dotnet-runic-dev-plan.txt"

  if ! (cd "$output" && dotnet runic doctor) > "$output/doctor.txt"; then
    cat "$output/doctor.txt" >&2
    exit 1
  fi
  grep -Fq "PASS package-manager: $manager $expected_manager_version matches certified baseline" "$output/doctor.txt"
  grep -Fq "PASS compatibility-set:" "$output/doctor.txt"

  # A plain build installs the frontend packages with the selected manager.
  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore
  test -d "$output/Frontend/node_modules"
  test -f "$output/Frontend/dist/index.html"
  # Runic Desktop serves each Window below its own path, where root-absolute
  # asset URLs leave the page blank. Desktop variants are not started here.
  if grep -nE '(src|href)="/[^/]' "$output/Frontend/dist/index.html"; then
    echo "The built frontend document references root-absolute URLs." >&2
    exit 1
  fi
  test -f "$output/Frontend/src/generated/workspace.ts"
  # runic({ desktop: true }) loads the Runic Desktop bootstrap; CS-WebUI does not serve it.
  if [[ "$framework" != angular ]]; then
    if [[ "$host" == desktop ]]; then
      grep -Fq '<script src="./runic-desktop.js"></script>' "$output/Frontend/dist/index.html"
    elif grep -Fq 'runic-desktop.js' "$output/Frontend/dist/index.html"; then
      echo "The CS-WebUI frontend loads the Runic Desktop bootstrap." >&2
      exit 1
    fi
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
  if [[ "$framework" == vue && "$manager" == bun ]]; then
    # vue-tsc finds no .vue files under the Bun runtime and passes, so check
    # with Node as a plain `bun run` does.
    (cd "$output/Frontend" && bun run typecheck)
  else
    frontend_script "$manager" "$output/Frontend" typecheck
  fi
  if [[ "$host" == cswebui ]]; then
    serve_and_fetch "$output" "$output/serve-release.log" "$output/release-document.html" false \
      dotnet run --project "$output/$project_name.csproj" --configuration Release --no-build
  fi
  printf 'TEMPLATE_OK|%s|%s|%s|%s\n' "$framework" "$manager" "$host" "$view_models"
}

# The guided creator must produce exactly what its printed dotnet new command
# produces, using the packaged tool and the candidate template.
verify_creator() {
  local creator_directory="$template_tmp/creator"
  local options=(--frontend vue --package-manager pnpm --host desktop --view-models reactiveui)
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
  grep -Fq 'react, vue, svelte, angular' "$creator_directory/invalid.txt"
  echo 'CREATOR_OK'
}

# Project names that are not npm names still produce a valid frontend package name.
verify_package_names() {
  local names_directory="$template_tmp/package-names"
  local project_name expected generated
  mkdir -p "$names_directory"
  for case_entry in "Contoso.Notes App|contoso-notes-app" "_My__API.|my-api" "Café Notes|caf-notes"; do
    project_name="${case_entry%%|*}"
    expected="${case_entry#*|}"
    (cd "$names_directory" && dotnet new runic-app --name "$project_name" --output "$expected" --frontend svelte)
    generated="$(bun -e 'process.stdout.write(require(process.argv[1]).name)' "$names_directory/$expected/Frontend/package.json")"
    if [[ "$generated" != "$expected" ]]; then
      echo "Project '$project_name' produced frontend package name '$generated', expected '$expected'." >&2
      exit 1
    fi
  done
  echo 'PACKAGE_NAMES_OK'
}

frameworks=(react vue svelte angular)
managers=(npm pnpm bun)
# Every frontend and package manager uses the default host and ViewModels.
# Pairwise variants then cover each frontend and package manager with Runic
# Desktop and ReactiveUI, and every host and ViewModel pairing.
variants=(
  "react pnpm desktop reactiveui"
  "vue bun desktop reactiveui"
  "svelte npm desktop reactiveui"
  "angular pnpm desktop reactiveui"
  "svelte bun desktop toolkit"
  "angular npm cswebui reactiveui"
)
# Keep CI's default matrix complete while allowing focused local debugging.
if [[ -n "${RUNIC_TEMPLATE_FRAMEWORKS:-}" ]]; then
  read -r -a frameworks <<< "$RUNIC_TEMPLATE_FRAMEWORKS"
fi
if [[ -n "${RUNIC_TEMPLATE_MANAGERS:-}" ]]; then
  read -r -a managers <<< "$RUNIC_TEMPLATE_MANAGERS"
fi
if [[ -n "${RUNIC_TEMPLATE_VARIANTS+set}" ]]; then
  # Semicolon-separated "frontend manager host view-models" entries; empty skips them.
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
for framework in "${frameworks[@]}"; do
  case "$framework" in
    react|vue|svelte|angular) ;;
    *) echo "Unknown template framework: $framework" >&2; exit 2 ;;
  esac
done
for manager in "${managers[@]}"; do
  case "$manager" in
    npm|pnpm|bun) ;;
    *) echo "Unknown template package manager: $manager" >&2; exit 2 ;;
  esac
done

# CI runs the creator and package name checks in one framework lane only.
case "${RUNIC_TEMPLATE_CREATOR:-1}" in
  1) verify_creator; verify_package_names ;;
  0) ;;
  *) echo "RUNIC_TEMPLATE_CREATOR must be 0 or 1." >&2; exit 2 ;;
esac
for manager in "${managers[@]}"; do
  for framework in "${frameworks[@]}"; do
    verify_template "$framework" "$manager" cswebui toolkit
  done
done
for variant in "${variants[@]}"; do
  read -r -a fields <<< "$variant"
  verify_template "${fields[@]}"
done
