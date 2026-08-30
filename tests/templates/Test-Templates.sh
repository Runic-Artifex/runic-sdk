#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 7 ]]; then
  echo "Usage: $0 <package-version> <package-directory> <application-bridge-tgz> <runic-angular-tgz> <runic-svelte-tgz> <runic-vite-tgz> <runic-desktop-tgz>" >&2
  exit 2
fi

package_version="$1"
package_directory="$(cd "$2" && pwd)"
npm_archive="$(realpath "$3")"
angular_archive="$(realpath "$4")"
svelte_archive="$(realpath "$5")"
vite_archive="$(realpath "$6")"
desktop_archive="$(realpath "$7")"
script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
template_package="$package_directory/Runic.Application.Templates.$package_version.nupkg"
template_tmp="$(mktemp -d /tmp/runic-toolkit-templates.XXXXXXXXXX)"
registry_pid=""

cleanup() {
  if [[ -n "$registry_pid" ]]; then
    kill "$registry_pid" 2>/dev/null || true
    wait "$registry_pid" 2>/dev/null || true
  fi
  case "$template_tmp" in
    /tmp/runic-toolkit-templates.*) rm -rf -- "$template_tmp" ;;
    *) echo "Refusing to remove unexpected path: $template_tmp" >&2 ;;
  esac
}
trap cleanup EXIT

if [[ ! -f "$template_package" || ! -f "$npm_archive" || ! -f "$angular_archive" || ! -f "$svelte_archive" || ! -f "$vite_archive" || ! -f "$desktop_archive" ]]; then
  echo "One or more template npm archives are missing." >&2
  exit 1
fi

export DOTNET_CLI_HOME="$template_tmp/dotnet-home"
export NUGET_PACKAGES="$template_tmp/nuget"
restore_sources=(--source "$package_directory" --source https://api.nuget.org/v3/index.json)
if [[ -n "${RUNIC_VERIFICATION_FEED:-}" ]]; then
  restore_sources=(--source "$RUNIC_VERIFICATION_FEED" "${restore_sources[@]}")
fi
dotnet new install "$template_package" --force
tool_directory="$template_tmp/tools"
dotnet tool install dotnet-runic --tool-path "$tool_directory" --version "$package_version" --add-source "$package_directory"

npm_archive_version() {
  node -e '
    const { execFileSync } = require("node:child_process");
    const manifest = JSON.parse(execFileSync("tar", ["-xOf", process.argv[1], "package/package.json"], { encoding: "utf8" }));
    process.stdout.write(manifest.version);
  ' "$1"
}

bridge_npm_version="$(npm_archive_version "$npm_archive")"
svelte_npm_version="$(npm_archive_version "$svelte_archive")"
vite_npm_version="$(npm_archive_version "$vite_archive")"

bind_candidate_integrities() {
  node "$script_directory/bind-template-candidate-integrities.mjs" "$1" \
    "$npm_archive" "$angular_archive" "$svelte_archive" "$vite_archive" "$desktop_archive"
}

registry_ready="$template_tmp/template-npm-registry.url"
node "$script_directory/template-npm-registry.mjs" \
  "$registry_ready" "$npm_archive" "$angular_archive" "$svelte_archive" "$vite_archive" "$desktop_archive" &
registry_pid=$!
for _ in $(seq 1 100); do
  [[ -s "$registry_ready" ]] && break
  sleep 0.05
done
[[ -s "$registry_ready" ]]
registry_url="$(<"$registry_ready")"

default_output="$template_tmp/default-react"
dotnet new runic-app-react --name PackagedDefaults --output "$default_output"
bind_candidate_integrities "$default_output/Frontend/package-lock.json"
grep -Fq "Version=\"$package_version\"" "$default_output/PackagedDefaults.csproj"
grep -Fq 'Version="1.0.0-preview.1"' "$default_output/PackagedDefaults.csproj"
npm --prefix "$default_output/Frontend" config set @runic-artifex:registry "$registry_url"
dotnet restore "$default_output/PackagedDefaults.csproj" "${restore_sources[@]}"
dotnet build "$default_output/PackagedDefaults.csproj" --configuration Release --no-restore
test -f "$default_output/Frontend/dist/index.html"
test -f "$default_output/Frontend/node_modules/.package-lock.json"

default_svelte_output="$template_tmp/default-svelte"
dotnet new runic-app-svelte --name PackagedSvelteDefaults --output "$default_svelte_output"
bind_candidate_integrities "$default_svelte_output/Frontend/package-lock.json"
node -e '
  const fs = require("node:fs");
  const manifest = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
  const expected = new Map([
    ["@runic-artifex/application-bridge", process.argv[2]],
    ["@runic-artifex/svelte", process.argv[3]],
    ["@runic-artifex/vite-plugin-runic", process.argv[4]],
  ]);
  for (const [name, version] of expected) {
    const actual = manifest.dependencies[name] ?? manifest.devDependencies[name];
    if (actual !== version) throw new Error(`${name} default was ${actual}, expected ${version}.`);
  }
' "$default_svelte_output/Frontend/package.json" "$bridge_npm_version" "$svelte_npm_version" "$vite_npm_version"
npm --prefix "$default_svelte_output/Frontend" config set @runic-artifex:registry "$registry_url"
dotnet restore "$default_svelte_output/PackagedSvelteDefaults.csproj" "${restore_sources[@]}"
dotnet build "$default_svelte_output/PackagedSvelteDefaults.csproj" --configuration Release --no-restore
dotnet run --project "$default_svelte_output/PackagedSvelteDefaults.csproj" \
  --configuration Release --no-build -- --smoke-test

for framework in react vue svelte angular; do
  project_name="Acceptance${framework^}"
  output="$template_tmp/$framework"
  template_arguments=(
    --name "$project_name"
    --output "$output"
    --runicApplicationVersion "$package_version"
    --runicAssetsVersion "1.0.0-preview.1"
  )
  dotnet new "runic-app-$framework" \
    "${template_arguments[@]}"
  bind_candidate_integrities "$output/Frontend/package-lock.json"
  "$tool_directory/dotnet-runic" dev --project "$output/$project_name.csproj" --dry-run -- --template-option -1 > "$output/dotnet-runic-dev-plan.txt"
  grep -Fq 'Frontend' "$output/dotnet-runic-dev-plan.txt"
  npm --prefix "$output/Frontend" config set @runic-artifex:registry "$registry_url"
  dotnet restore "$output/$project_name.csproj" "${restore_sources[@]}"
  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore
  test -f "$output/Frontend/dist/index.html"
  test -f "$output/Frontend/node_modules/.package-lock.json"
  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore > "$output/incremental.log"
  if grep -Fq '[runic] Building managed frontend assets.' "$output/incremental.log"; then
    echo "The unchanged $framework frontend was rebuilt by the managed project." >&2
    exit 1
  fi
  touch "$output/Frontend/src/main.ts" "$output/Frontend/src/main.tsx" 2>/dev/null || true
  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore > "$output/rebuild.log"
  grep -Fq '[runic] Building managed frontend assets.' "$output/rebuild.log"
  first_manifest="$($tool_directory/dotnet-runic inspect --project "$output/$project_name.csproj" --configuration Release)"
  second_manifest="$($tool_directory/dotnet-runic inspect --project "$output/$project_name.csproj" --configuration Release)"
  [[ "$first_manifest" == "$second_manifest" ]]
  grep -Fq '"schema":"runic.application/1"' <<< "$first_manifest"
  grep -Fq '"provenance":"template"' <<< "$first_manifest"
  dotnet run --project "$output/$project_name.csproj" \
    --configuration Release --no-build -- --smoke-test
done
