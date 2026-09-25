#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "Usage: $0 <package-version> <package-directory> <views-angular.tgz> <views-svelte.tgz>" >&2
  exit 2
fi

package_version="$1"
package_directory="$(cd "$2" && pwd)"
angular_archive="$(realpath "$3")"
svelte_archive="$(realpath "$4")"
script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_directory/../.." && pwd)"
template_package="$package_directory/Runic.Application.Templates.$package_version.nupkg"
template_tmp="$(mktemp -d /tmp/runic-views-templates.XXXXXXXXXX)"
registry_pid=""

cleanup() {
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

for required in "$template_package" "$angular_archive" "$svelte_archive"; do
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

views_angular_version="$(npm_archive_version "$angular_archive")"
views_svelte_version="$(npm_archive_version "$svelte_archive")"
registry_ready="$template_tmp/template-npm-registry.url"
bun "$script_directory/template-npm-registry.mjs" \
  "$registry_ready" "$angular_archive" "$svelte_archive" &
registry_pid=$!
for _ in $(seq 1 100); do
  [[ -s "$registry_ready" ]] && break
  sleep 0.05
done
[[ -s "$registry_ready" ]]
registry_url="$(<"$registry_ready")"

configure_candidate_registry() {
  local framework="$1"
  local manager="$2"
  local output="$3"
  if [[ "$framework" == svelte ]]; then
    (cd "$output/Frontend" && npm config set --location=project @runic-artifex:registry "$registry_url")
    if [[ "$manager" == npm ]]; then
      RUNIC_TEMPLATE_NPM_REGISTRY="$registry_url" bun "$script_directory/bind-template-candidate-integrities.mjs" \
        "$output/Frontend/package-lock.json" "$svelte_archive"
    fi
  elif [[ "$framework" == angular ]]; then
    (cd "$output/Frontend" && npm config set --location=project @runic-artifex:registry "$registry_url")
    if [[ "$manager" == npm ]]; then
      RUNIC_TEMPLATE_NPM_REGISTRY="$registry_url" bun "$script_directory/bind-template-candidate-integrities.mjs" \
        "$output/Frontend/package-lock.json" "$angular_archive"
    fi
  fi
}

frontend_install() {
  local manager="$1"
  local frontend="$2"
  case "$manager" in
    npm) (cd "$frontend" && npm ci --no-audit --no-fund) ;;
    pnpm) (cd "$frontend" && pnpm install --frozen-lockfile --ignore-scripts) ;;
    bun) (cd "$frontend" && bun install --frozen-lockfile) ;;
  esac
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

verify_template() {
  local framework="$1"
  local manager="$2"
  local project_name="Acceptance${framework^}${manager^}"
  local output="$template_tmp/$framework-$manager"
  local expected_manager_version
  local selected_lock
  case "$manager" in
    npm) expected_manager_version="$(npm --version)"; selected_lock=package-lock.json ;;
    pnpm) expected_manager_version="$pnpm_version"; selected_lock=pnpm-lock.yaml ;;
    bun) expected_manager_version="$bun_version"; selected_lock=bun.lock ;;
  esac

  template_options=(
    --name "$project_name"
    --output "$output"
    --packageManager "$manager"
    --runicViewsVersion "$package_version"
    --dotnetRunicVersion "$package_version"
  )
  case "$framework" in
    angular) template_options+=(--viewsAngularVersion "$views_angular_version") ;;
    svelte) template_options+=(--viewsSvelteVersion "$views_svelte_version") ;;
  esac
  dotnet new "runic-app-$framework" "${template_options[@]}"

  test -f "$output/Frontend/$selected_lock"
  for other_lock in package-lock.json pnpm-lock.yaml bun.lock; do
    [[ "$other_lock" == "$selected_lock" ]] || test ! -f "$output/Frontend/$other_lock"
  done
  grep -Fq "\"packageManager\": \"$manager@$expected_manager_version\"" "$output/Frontend/package.json"
  grep -Fq 'RunicViewsWindowProject>true' "$output/$project_name.csproj"
  grep -Fq 'Runic.Application.CsWebUi' "$output/$project_name.csproj"
  if rg -ni 'Runic\.Application\.Bridge|Runic\.Desktop|application-bridge|@runic-artifex/(views-angular|views-svelte|desktop)' "$output"; then
    echo "The generated $framework template contains a removed Bridge or Desktop package." >&2
    exit 1
  fi

  dotnet tool restore \
    --tool-manifest "$output/.config/dotnet-tools.json" \
    "${tool_source_options[@]}" \
    "${tool_restore_options[@]}"
  configure_candidate_registry "$framework" "$manager" "$output"
  frontend_install "$manager" "$output/Frontend"
  dotnet restore "$output/$project_name.csproj" "${restore_sources[@]}"

  "$tool_directory/dotnet-runic" dev \
    --project "$output/$project_name.csproj" \
    --dry-run > "$output/dotnet-runic-dev-plan.txt"
  grep -Fq 'Runic Views Window project' "$output/dotnet-runic-dev-plan.txt"

  if ! (cd "$output" && dotnet runic doctor --project "$output/$project_name.csproj") > "$output/doctor.txt"; then
    cat "$output/doctor.txt" >&2
    exit 1
  fi
  grep -Fq "PASS package-manager: $manager $expected_manager_version matches certified baseline" "$output/doctor.txt"

  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore
  test -f "$output/Frontend/dist/index.html"
  frontend_script "$manager" "$output/Frontend" typecheck
  if [[ "$manager" == npm ]]; then
    dotnet run --project "$output/$project_name.csproj" \
      --configuration Release --no-build -- --smoke-test | grep -Fq 'RUNIC_VIEWS_TEMPLATE_OK|window|view|command'
  fi
  printf 'TEMPLATE_OK|%s|%s\n' "$framework" "$manager"
}

frameworks=(react vue svelte angular)
managers=(npm pnpm bun)
# Keep CI's default matrix complete while allowing focused local debugging.
if [[ -n "${RUNIC_TEMPLATE_FRAMEWORKS:-}" ]]; then
  read -r -a frameworks <<< "$RUNIC_TEMPLATE_FRAMEWORKS"
fi
if [[ -n "${RUNIC_TEMPLATE_MANAGERS:-}" ]]; then
  read -r -a managers <<< "$RUNIC_TEMPLATE_MANAGERS"
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

for manager in "${managers[@]}"; do
  for framework in "${frameworks[@]}"; do
    verify_template "$framework" "$manager"
  done
done
