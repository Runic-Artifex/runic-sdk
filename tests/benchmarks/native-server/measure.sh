#!/usr/bin/env bash
# Size, startup and memory of examples/first-window-desktop on Kestrel and on the experimental native server (X1),
# published for linux-x64 as framework-dependent, self-contained, self-contained trimmed and NativeAOT.
#
#   tests/benchmarks/native-server/measure.sh [output-directory]
#
# Needs eng/native/build-runic-server.sh linux-x64 first. Writes publish directories and results.json under the
# output directory (artifacts/native-server-measure by default) and prints a Markdown table. Startup and memory come
# from startup.mjs (serve-only, median of 15 runs); <server>-aot-code.json attributes the NativeAOT code to assembly
# families (attribute.mjs).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
out="${1:-$root/artifacts/native-server-measure}"
project="$root/examples/first-window-desktop/FirstWindowDesktop.csproj"
rid=linux-x64
runs="${RUNIC_MEASURE_RUNS:-15}"
mkdir -p "$out"
results="$out/results.jsonl"
[[ -n "${RUNIC_MEASURE_APPEND:-}" ]] || : >"$results"

publish() {
  local server="$1" mode="$2" directory="$out/$1-$2"
  local -a properties=(-p:RunicDesktopServer="$server")
  case "$mode" in
    fdd) properties+=(--self-contained false) ;;
    sc) properties+=(--self-contained true) ;;
    trimmed) properties+=(--self-contained true -p:PublishTrimmed=true) ;;
    aot) properties+=(-p:PublishAot=true -p:IlcGenerateMapFile=true) ;;
  esac
  rm -rf "$directory"
  dotnet publish "$project" -c Release -r "$rid" -o "$directory" "${properties[@]}" -nologo -v:q >"$out/$server-$mode.log" 2>&1 \
    || { tail -30 "$out/$server-$mode.log"; return 1; }
  local total executable aspnet
  total=$(find "$directory" -type f ! -name '*.pdb' ! -name '*.dbg' ! -name '*.xml' -printf '%s\n' | awk '{ s += $1 } END { print s }')
  executable=$(stat -c %s "$directory/FirstWindowDesktop")
  # The ASP.NET Core shared framework's files the publish carries (self-contained modes).
  aspnet=$(find "$directory" -maxdepth 1 -type f -name 'Microsoft.AspNetCore.*.dll' -printf '%s\n' | awk '{ s += $1 } END { print s + 0 }')
  if [[ "$mode" == aot ]]; then
    local native="$root/examples/first-window-desktop/obj/Release/net10.0/$rid/native"
    node "$root/tests/benchmarks/native-server/attribute.mjs" "$native/FirstWindowDesktop.map.xml" >"$out/$server-aot-code.json"
  fi
  local startup
  # A build that does not start is recorded, not fatal: the table then shows "fails".
  startup=$(node "$root/tests/benchmarks/native-server/startup.mjs" "$directory/FirstWindowDesktop" "$runs" 2>"$out/$server-$mode.startup.log") \
    || startup='{"failed":true}'
  if [[ "$startup" == '{"failed":true}' ]]; then
    grep -m1 -o 'Unhandled exception[^\]*' "$out/$server-$mode.startup.log" | head -c 300 >&2 || true
  fi
  printf '{"server":"%s","mode":"%s","totalBytes":%s,"executableBytes":%s,"aspNetCoreBytes":%s,"startup":%s}\n' \
    "$server" "$mode" "$total" "$executable" "$aspnet" "$startup" >>"$results"
  echo "$server $mode: $total bytes, startup $startup" >&2
}

for mode in ${RUNIC_MEASURE_MODES:-fdd sc trimmed aot}; do
  for server in Kestrel Native; do
    publish "$server" "$mode"
  done
done

node - "$results" <<'NODE'
const rows = require("node:fs").readFileSync(process.argv[2], "utf8").trim().split("\n").map(JSON.parse);
const mib = bytes => (bytes / 1048576).toFixed(2);
console.log("| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | URL ready (ms) | Page served (ms) | RSS (MiB) | Peak RSS (MiB) |");
console.log("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
for (const row of rows)
  console.log(`| ${row.mode} | ${row.server} | ${mib(row.totalBytes)} | ${mib(row.executableBytes)} | ${mib(row.aspNetCoreBytes)} | ` +
    (row.startup.failed ? "fails to start | | | |" : `${row.startup.listeningMs} | ${row.startup.servedMs} | ${row.startup.rssMiB} | ${row.startup.peakMiB} |`));
NODE
