#!/usr/bin/env bash
set -euo pipefail

experiment_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(git -C "$experiment_root" rev-parse --show-toplevel)"
: "${CS_WEBUI_REPOSITORY:?Set CS_WEBUI_REPOSITORY to an explicit cs-webui checkout}"
cs_repository="$(cd "$CS_WEBUI_REPOSITORY" && pwd)"
if [[ ! -f "$cs_repository/src/CsWebUi/CsWebUi.csproj" ]]; then
  echo "CS_WEBUI_REPOSITORY does not contain src/CsWebUi/CsWebUi.csproj." >&2
  exit 2
fi
cd "$repository_root"
artifacts_root="$repository_root/artifacts/native-aot-size/linux-x64"
mkdir -p "$artifacts_root"
run_root="$(mktemp -d "$artifacts_root/run.XXXXXXXX")"
cs_output="$run_root/cs-webui"
runic_output="$run_root/runic-desktop"
aspnet_output="$run_root/aspnetcore-diagnostic"

mkdir -p "$cs_output" "$runic_output" "$aspnet_output"

sdk_version="$(dotnet --version)"
if [[ "$sdk_version" != 10.0.302 ]]; then
  echo "Expected the repository SDK 10.0.302, but dotnet resolves to $sdk_version." >&2
  echo "Run from the SDK environment: nix develop --command $0" >&2
  exit 2
fi

publish_arguments=(
  --configuration Release
  --runtime linux-x64
  -p:PublishAot=true
  -p:PublishTrimmed=true
  -p:TrimMode=full
  -p:SelfContained=true
  -p:InvariantGlobalization=true
  -p:OptimizationPreference=Size
  -p:StripSymbols=true
  -p:DebugType=None
  -p:DebugSymbols=false
)

dotnet publish "$experiment_root/CsWebUiBaseline/CsWebUiBaseline.csproj" \
  "${publish_arguments[@]}" \
  "-p:CsWebUiRepository=$cs_repository" \
  --output "$cs_output" \
  2>&1 | tee "$run_root/cs-webui-publish.log"

dotnet publish "$experiment_root/RunicDesktopBaseline/RunicDesktopBaseline.csproj" \
  "${publish_arguments[@]}" \
  --output "$runic_output" \
  2>&1 | tee "$run_root/runic-desktop-publish.log"

dotnet publish "$experiment_root/AspNetCoreDiagnostic/AspNetCoreDiagnostic.csproj" \
  "${publish_arguments[@]}" \
  --output "$aspnet_output" \
  2>&1 | tee "$run_root/aspnetcore-diagnostic-publish.log"

cs_executable="$cs_output/DesktopSizeBaseline"
runic_executable="$runic_output/DesktopSizeBaseline"
aspnet_executable="$aspnet_output/AspNetCoreDiagnostic"
cs_native_library="$cs_output/libwebui-2.so"

for required_file in "$cs_executable" "$runic_executable" "$aspnet_executable" "$cs_native_library"; do
  if [[ ! -f "$required_file" ]]; then
    echo "Expected publish artifact is missing: $required_file" >&2
    exit 1
  fi
done

if [[ "${RUNIC_NATIVE_AOT_SKIP_SMOKE:-0}" != "1" ]]; then
  timeout 40s xvfb-run -a "$cs_executable" | tee "$run_root/cs-webui-smoke.log"
  timeout 40s xvfb-run -a "$runic_executable" | tee "$run_root/runic-desktop-smoke.log"
fi

directory_bytes() {
  find "$1" -type f -printf '%s\n' | awk '{ total += $1 } END { print total + 0 }'
}

archive_directory() {
  local source_directory="$1"
  local archive_path="$2"
  tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
    -C "$source_directory" -cf - . | gzip -9 -n > "$archive_path"
}

archive_directory "$cs_output" "$run_root/cs-webui.tar.gz"
archive_directory "$runic_output" "$run_root/runic-desktop.tar.gz"
archive_directory "$aspnet_output" "$run_root/aspnetcore-diagnostic.tar.gz"
tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
  -C "$cs_output" -cf - DesktopSizeBaseline libwebui-2.so \
  | gzip -9 -n > "$run_root/cs-webui-runtime.tar.gz"
gzip -9 -n -c "$runic_executable" > "$run_root/runic-desktop-runtime.gz"

cs_total="$(directory_bytes "$cs_output")"
runic_total="$(directory_bytes "$runic_output")"
cs_executable_size="$(stat --format='%s' "$cs_executable")"
runic_executable_size="$(stat --format='%s' "$runic_executable")"
cs_native_size="$(stat --format='%s' "$cs_native_library")"
cs_runtime_size="$((cs_executable_size + cs_native_size))"
runic_runtime_size="$runic_executable_size"
cs_compressed="$(stat --format='%s' "$run_root/cs-webui.tar.gz")"
runic_compressed="$(stat --format='%s' "$run_root/runic-desktop.tar.gz")"
cs_runtime_compressed="$(stat --format='%s' "$run_root/cs-webui-runtime.tar.gz")"
runic_runtime_compressed="$(stat --format='%s' "$run_root/runic-desktop-runtime.gz")"
delta="$((runic_total - cs_total))"
ratio="$(awk -v runic="$runic_total" -v cs="$cs_total" 'BEGIN { printf "%.2f", runic / cs }')"
compressed_delta="$((runic_compressed - cs_compressed))"
compressed_ratio="$(awk -v runic="$runic_compressed" -v cs="$cs_compressed" 'BEGIN { printf "%.2f", runic / cs }')"

{
  printf 'metric\tcs-webui\trunic-desktop\tdelta\tratio\n'
  printf 'published-bytes\t%s\t%s\t%s\t%s\n' "$cs_total" "$runic_total" "$delta" "$ratio"
  printf 'main-executable-bytes\t%s\t%s\t%s\t%s\n' \
    "$cs_executable_size" \
    "$runic_executable_size" \
    "$((runic_executable_size - cs_executable_size))" \
    "$(awk -v runic="$runic_executable_size" -v cs="$cs_executable_size" 'BEGIN { printf "%.2f", runic / cs }')"
  printf 'compressed-bytes\t%s\t%s\t%s\t%s\n' \
    "$cs_compressed" "$runic_compressed" "$compressed_delta" "$compressed_ratio"
  printf 'runtime-payload-bytes\t%s\t%s\t%s\t%s\n' \
    "$cs_runtime_size" \
    "$runic_runtime_size" \
    "$((runic_runtime_size - cs_runtime_size))" \
    "$(awk -v runic="$runic_runtime_size" -v cs="$cs_runtime_size" 'BEGIN { printf "%.2f", runic / cs }')"
  printf 'compressed-runtime-payload-bytes\t%s\t%s\t%s\t%s\n' \
    "$cs_runtime_compressed" \
    "$runic_runtime_compressed" \
    "$((runic_runtime_compressed - cs_runtime_compressed))" \
    "$(awk -v runic="$runic_runtime_compressed" -v cs="$cs_runtime_compressed" 'BEGIN { printf "%.2f", runic / cs }')"
  printf 'cs-webui-native-library-bytes\t%s\t0\t-%s\t0.00\n' "$cs_native_size" "$cs_native_size"
} | tee "$run_root/measurements.tsv"

aspnet_size="$(stat --format='%s' "$aspnet_executable")"
aspnet_compressed="$(stat --format='%s' "$run_root/aspnetcore-diagnostic.tar.gz")"
{
  printf 'metric\tbytes\n'
  printf 'aspnetcore-diagnostic\t%s\n' "$aspnet_size"
  printf 'aspnetcore-diagnostic-compressed\t%s\n' "$aspnet_compressed"
  printf 'runic-over-aspnetcore\t%s\n' "$((runic_executable_size - aspnet_size))"
  printf 'runic-over-aspnetcore-percent\t%s\n' \
    "$(awk -v runic="$runic_executable_size" -v aspnet="$aspnet_size" 'BEGIN { printf "%.2f", ((runic - aspnet) / aspnet) * 100 }')"
} | tee "$run_root/diagnostic.tsv"

{
  printf 'cs-webui files\n'
  find "$cs_output" -type f -printf '%s\t%P\n' | sort -nr
  printf '\nRunic Desktop files\n'
  find "$runic_output" -type f -printf '%s\t%P\n' | sort -nr
  printf '\nASP.NET Core diagnostic files\n'
  find "$aspnet_output" -type f -printf '%s\t%P\n' | sort -nr
} | tee "$run_root/files.txt"

{
  dotnet --info
  uname -a
  printf '\nSDK working status\n'
  git -C "$repository_root" status --porcelain
  printf '\nCS-WebUI working status\n'
  git -C "$cs_repository" status --porcelain
  printf '\nSmoke skipped\t%s\n' "${RUNIC_NATIVE_AOT_SKIP_SMOKE:-0}"
  printf '\nrunic-sdk\t%s\n' "$(git -C "$repository_root" rev-parse HEAD)"
  printf 'cs-webui\t%s\n' "$(git -C "$cs_repository" rev-parse HEAD)"
} > "$run_root/environment.txt"

printf '\nComparison artifacts: %s\n' "$run_root"
