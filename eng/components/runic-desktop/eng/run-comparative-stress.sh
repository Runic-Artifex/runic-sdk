#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cs_webui_repository=${RUNIC_CS_WEBUI_REPOSITORY:-"$repository_root/../cs-webui"}
expected_cs_webui_revision=$(tr -d '[:space:]' < "$repository_root/eng/comparative-stress/cs-webui-revision.txt")
output=${1:-"$repository_root/artifacts/comparative-stress.json"}
working_directory=$(mktemp -d "${TMPDIR:-/tmp}/runic-comparative-stress.XXXXXX")

require_clean_tree() {
  local repository=$1
  if ! git -C "$repository" diff --quiet \
    || ! git -C "$repository" diff --cached --quiet \
    || [[ -n "$(git -C "$repository" ls-files --others --exclude-standard)" ]]; then
    printf 'Comparative stress requires a clean source tree: %s\n' "$repository" >&2
    exit 1
  fi
}

cleanup() {
  rm -rf "$working_directory"
}
trap cleanup EXIT

require_clean_tree "$repository_root"
require_clean_tree "$cs_webui_repository"
actual_cs_webui_revision=$(git -C "$cs_webui_repository" rev-parse HEAD)
if [[ "$actual_cs_webui_revision" != "$expected_cs_webui_revision" ]]; then
  printf 'CS-WebUI baseline is %s; expected %s.\n' "$actual_cs_webui_revision" "$expected_cs_webui_revision" >&2
  exit 1
fi
mkdir -p "$(dirname "$output")"
dotnet publish "$repository_root/tests/Runic.Desktop.ComparativeStressAdapter/Runic.Desktop.ComparativeStressAdapter.csproj" --configuration Release --output "$working_directory/desktop"
dotnet publish "$cs_webui_repository/samples/CsWebUi.ComparativeStressAdapter/CsWebUi.ComparativeStressAdapter.csproj" --configuration Release --output "$working_directory/cs-webui"
node "$repository_root/eng/comparative-stress/run.mjs" run \
  --workload "$repository_root/eng/comparative-stress/workload.json" \
  --desktop "$working_directory/desktop/Runic.Desktop.ComparativeStressAdapter.dll" \
  --desktop-revision "$(git -C "$repository_root" rev-parse HEAD)" \
  --cs-webui "$working_directory/cs-webui/CsWebUi.ComparativeStressAdapter.dll" \
  --cs-webui-revision "$actual_cs_webui_revision" \
  --dotnet-sdk "$(dotnet --version)" \
  --output "$output"
node "$repository_root/eng/comparative-stress/run.mjs" verify "$output"
printf 'Comparative stress observation: %s\n' "$output"
