#!/usr/bin/env bash
set -euo pipefail

# Run from the repository's locked development shell. Work on a disposable copy
# so the source edit never changes the committed fixture or a developer's build.
fixture_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
probe_dir="$(mktemp -d -t runic-dev-restart-XXXXXX)"
watch_pid=""
cleanup() {
  if [[ -n "$watch_pid" ]]; then
    kill -TERM -- "-$watch_pid" 2>/dev/null || true
    wait "$watch_pid" 2>/dev/null || true
  fi
  rm -rf -- "$probe_dir"
}
trap cleanup EXIT

mkdir -p "$probe_dir/app"
cp "$fixture_dir/DevRestartOwnership.csproj" "$fixture_dir/Contract.cs" \
   "$fixture_dir/Program.cs" "$probe_dir/app/"
project="$probe_dir/app/DevRestartOwnership.csproj"
ready="$probe_dir/app/obj/Debug/net10.0/view-bridge.ready.json"
host_ready="$probe_dir/host-ready.fingerprint"
starts="$probe_dir/host-starts.txt"
log="$probe_dir/dotnet-watch.log"

dotnet build "$project" --configuration Debug >/dev/null
export RUNIC_VIEW_BRIDGE_HOST_READY="$host_ready"
export RUNIC_PROBE_STARTS_PATH="$starts"
export DOTNET_WATCH_RESTART_ON_RUDE_EDIT=1
setsid dotnet watch --project "$project" --configuration Debug --no-restore \
  --property:DebugType=portable --property:DebugSymbols=true \
  --property:Optimize=false \
  --property:RunicApplicationFrontendCompilerDevelopmentHotReload=true \
  --non-interactive run --no-launch-profile >"$log" 2>&1 &
watch_pid=$!

wait_for() {
  local label="$1" predicate="$2"
  for ((attempt = 0; attempt < 300; attempt++)); do
    if eval "$predicate"; then return 0; fi
    if ! kill -0 "$watch_pid" 2>/dev/null; then break; fi
    sleep 0.1
  done
  printf 'FAIL waiting for %s\n' "$label" >&2
  tail -80 "$log" >&2
  exit 1
}

wait_for 'first host start and acknowledgment' \
  '[[ -s "$starts" && -s "$host_ready" ]]'
initial_ready="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["fingerprint"])' "$ready")"
initial_host="$(tr -d '\n' < "$host_ready")"
[[ "$initial_ready" == "$initial_host" ]] || {
  printf 'FAIL first host fingerprint does not match the published manifest\n' >&2
  exit 1
}

sed -i 's/ContractBeforeEdit/ContractAfterEdit/' "$probe_dir/app/Contract.cs"
wait_for 'changed MSBuild ready manifest' \
  '[[ "$(python3 -c '\''import json,sys; print(json.load(open(sys.argv[1]))["fingerprint"])'\'' "$ready")" != "$initial_ready" ]]'
wait_for 'second host start' '[[ $(wc -l < "$starts") -ge 2 ]]'
sleep 4

start_count="$(wc -l < "$starts")"
changed_ready="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["fingerprint"])' "$ready")"
changed_host="$(tr -d '\n' < "$host_ready")"
if [[ "$start_count" != 2 || "$changed_ready" != "$changed_host" ]]; then
  printf 'FAIL host starts=%s, manifest=%s, host=%s\n' \
    "$start_count" "$changed_ready" "$changed_host" >&2
  cat "$starts" >&2
  tail -80 "$log" >&2
  exit 1
fi

first_pid="$(awk 'NR == 1 { print $1 }' "$starts")"
second_pid="$(awk 'NR == 2 { print $1 }' "$starts")"
[[ "$first_pid" != "$second_pid" ]] || {
  printf 'FAIL the second host start did not have a new process identity\n' >&2
  exit 1
}

printf 'DEV_RESTART_OWNERSHIP_OK|starts=%s|initial=%s|changed=%s|host-ack-matched\n' \
  "$start_count" "$initial_ready" "$changed_ready"
grep -E 'dotnet watch|PROBE_HOST_STARTED' "$log" | tail -25
