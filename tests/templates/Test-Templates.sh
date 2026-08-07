#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <package-version> <package-directory> <application-bridge-tgz>" >&2
  exit 2
fi

package_version="$1"
package_directory="$(cd "$2" && pwd)"
npm_archive="$(realpath "$3")"
template_package="$package_directory/RunicToolkit.Templates.$package_version.nupkg"
template_tmp="$(mktemp -d /tmp/runic-toolkit-templates.XXXXXXXXXX)"

cleanup() {
  case "$template_tmp" in
    /tmp/runic-toolkit-templates.*) rm -rf -- "$template_tmp" ;;
    *) echo "Refusing to remove unexpected path: $template_tmp" >&2 ;;
  esac
}
trap cleanup EXIT

if [[ ! -f "$template_package" || ! -f "$npm_archive" ]]; then
  echo "The template package or Application Bridge npm archive is missing." >&2
  exit 1
fi

export DOTNET_CLI_HOME="$template_tmp/dotnet-home"
export NUGET_PACKAGES="$template_tmp/nuget"
dotnet new install "$template_package" --force

for framework in react vue svelte angular; do
  project_name="Acceptance${framework^}"
  output="$template_tmp/$framework"
  dotnet new "runic-toolkit-$framework" \
    --name "$project_name" \
    --output "$output" \
    --runicToolkitVersion "$package_version" \
    --applicationBridgeNpm "file:$npm_archive"
  dotnet restore "$output/$project_name.csproj" \
    --source "$package_directory" \
    --source https://api.nuget.org/v3/index.json
  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore
  dotnet run --project "$output/$project_name.csproj" \
    --configuration Release --no-build -- --smoke-test
done
