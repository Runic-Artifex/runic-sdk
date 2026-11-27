#!/usr/bin/env bash
# Runs both halves of the split CI on one Linux machine, in two fresh copies of the repository, as the workflow runs
# them on two runners:
#
#   1. The Linux job, in linux/: dotnet test, then dotnet runic bundle, which builds the frontend with Node.js and
#      writes the web bundle, the job's artifact.
#   2. The Windows job, in windows/, with neither Node.js nor Bun on PATH: the bundle's web-bundle check (doctor), then
#      a framework-dependent win-x64 publish that takes its page from the bundle. Its www/ must be the bundle's page.
#      NativeAOT and the MSIX need a Windows runner; the publish runs the same contract check.
#   3. With --package deb, also without Node.js: dotnet runic package deb --frontend-bundle, NativeAOT on this machine.
#   4. The negative case: a ViewModel that changed after the bundle was written. The publish must stop with
#      RUNICBRIDGE094, naming the View and both fingerprints.
#
# Usage: ci/local-run.sh [--source <dir>] [--work <dir>] [--package deb] [--keep]
#   --source   the repository to copy, the folder above ci/ by default
#   --work     an empty or new folder for the copies, the bundle and the logs; a temporary one by default
#   --keep     keep the work folder
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_directory="$(cd "$script_directory/.." && pwd)"
work=""
package_target=""
keep=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --source) source_directory="$(cd "$2" && pwd)"; shift 2 ;;
    --work) work="$2"; shift 2 ;;
    --package) package_target="$2"; shift 2 ;;
    --keep) keep=1; shift ;;
    -h|--help) sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
case "$package_target" in
  ""|deb) ;;
  *) echo "--package takes deb, the Linux target this machine can build." >&2; exit 2 ;;
esac

if [[ -z "$work" ]]; then
  work="$(mktemp -d "${TMPDIR:-/tmp}/split-ci-local-run.XXXXXXXX")"
elif [[ -e "$work" && -n "$(ls -A "$work")" ]]; then
  echo "The work folder $work is not empty." >&2
  exit 2
fi
mkdir -p "$work/logs"
work="$(cd "$work" && pwd)"
cleanup() {
  if [[ "$keep" == 1 ]]; then
    echo "Kept $work"
  else
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

# A workflow job has no NODE_ENV; `bun test` sets NODE_ENV=test, which the frontend build would receive.
unset NODE_ENV

# Runs one step, prints its last lines when it fails. Output goes to logs/<name>.log.
step() {
  local name="$1"
  shift
  echo "== $name: $*"
  if ! "$@" > "$work/logs/$name.log" 2>&1; then
    tail -n 60 "$work/logs/$name.log" >&2
    echo "$name failed; the whole output is in $work/logs/$name.log (use --keep)." >&2
    return 1
  fi
}

# A fresh checkout: the repository's files without build output, installed packages or a bundle.
checkout() {
  mkdir -p "$1"
  tar -C "$source_directory" --exclude=.git --exclude=bin --exclude=obj --exclude=node_modules --exclude=dist \
    --exclude=artifacts --exclude=generated --exclude=TestResults -cf - . | tar -C "$1" -xf -
}

# PATH without Node.js, Bun and their package managers, as a Windows runner without them has: each PATH folder that
# has one of them is replaced by a folder of links to its other entries.
hidden_tools=(node nodejs bun bunx npm npx pnpm pnpx yarn corepack)
without_node_path() {
  local shadow_root="$work/no-node-path" result="" index=0 folder tool entry name has
  local -a folders
  IFS=: read -r -a folders <<< "$PATH"
  for folder in "${folders[@]}"; do
    [[ -n "$folder" && -d "$folder" ]] || continue
    has=0
    for tool in "${hidden_tools[@]}"; do [[ -e "$folder/$tool" ]] && has=1; done
    if [[ "$has" == 0 ]]; then
      result="${result:+$result:}$folder"
      continue
    fi
    local shadow="$shadow_root/$index"
    index=$((index + 1))
    mkdir -p "$shadow"
    for entry in "$folder"/*; do
      name="${entry##*/}"
      for tool in "${hidden_tools[@]}"; do [[ "$name" == "$tool" ]] && continue 2; done
      ln -s "$entry" "$shadow/$name"
    done
    result="${result:+$result:}$shadow"
  done
  for tool in "${hidden_tools[@]}"; do
    if PATH="$result" command -v "$tool" > /dev/null 2>&1; then
      echo "$tool is still on the PATH without Node.js." >&2
      return 1
    fi
  done
  printf '%s' "$result"
}

linux="$work/linux"
windows="$work/windows"
bundle="$work/web-bundle"
project=src/Acme.App/Acme.App.csproj
model=src/Acme.App/CounterViewModel.cs
page=src/Acme.App/Frontend

# 1. The Linux job.
checkout "$linux"
cd "$linux"
step tool-restore-linux dotnet tool restore
step test dotnet test --project tests/Acme.App.Tests/Acme.App.Tests.csproj -c Release
step bundle dotnet runic bundle --project src/Acme.App --output-directory "$bundle"
grep -E '^ *Acme\.App' "$work/logs/bundle.log" || true
[[ -f "$bundle/web-bundle.json" ]] || { echo "dotnet runic bundle wrote no web-bundle.json." >&2; exit 1; }
bundled="$(grep -oE '"fingerprint": *"[0-9A-F]{64}"' "$bundle/web-bundle.json" | grep -oE '[0-9A-F]{64}')"
[[ "$(wc -l <<< "$bundled")" == 1 ]] || { echo "Expected one View in the bundle, found: $bundled" >&2; exit 1; }

# 2. The Windows job, from a fresh checkout and the downloaded artifact, without Node.js.
checkout "$windows"
cd "$windows"
no_node_path="$(without_node_path)"
without_node() { env PATH="$no_node_path" "$@"; }
echo "== without_node: PATH without $(command -v node || echo node), $(command -v bun || echo bun) and their package managers"
step tool-restore-windows without_node dotnet tool restore
# Only doctor's web-bundle check counts here: its javascript-runtime and package-manager checks fail without Node.js.
without_node dotnet runic doctor --project src/Acme.App --bundle "$bundle" --format json > "$work/logs/doctor.json" 2> "$work/logs/doctor.log" || true
if ! tr -d '\n' < "$work/logs/doctor.json" | grep -qE '"id": *"web-bundle", *"status": *"pass"'; then
  cat "$work/logs/doctor.json" "$work/logs/doctor.log" >&2
  echo "doctor's web-bundle check did not pass." >&2
  exit 1
fi
echo "== doctor --bundle: the web-bundle check passes"
step publish-win-x64 without_node dotnet publish "$project" -c Release -r win-x64 --self-contained false \
  "-p:RunicApplicationFrontendPrebuiltDirectory=$bundle" -o "$work/out-win"
grep -E 'Web bundle: ' "$work/logs/publish-win-x64.log" || { echo "The publish did not take the page from the bundle." >&2; exit 1; }
if grep -E 'RUNICBRIDGE084|warning RUNICBRIDGE' "$work/logs/publish-win-x64.log"; then
  echo "The publish needed Node.js or warned." >&2
  exit 1
fi
[[ -f "$work/out-win/Acme.App.exe" ]] || { echo "The publish wrote no Acme.App.exe." >&2; exit 1; }
diff -r --exclude=runic-views.js "$bundle/projects/Acme.App/www" "$work/out-win/www"
[[ -f "$work/out-win/www/runic-views.js" ]]
# No install and no client emission ran.
[[ ! -e "$page/node_modules" && ! -e "$page/src/generated" ]] || { echo "The publish installed the frontend or emitted clients." >&2; exit 1; }
echo "== www/ is the bundle's page, plus the host's runic-views.js"

# 3. Optional: a Linux package from the same bundle, through the tool.
if [[ "$package_target" == deb ]]; then
  step package-deb without_node dotnet runic package deb --project src/Acme.App --frontend-bundle "$bundle" \
    --output-directory "$work/deb"
  ls "$work"/deb/*.deb
fi

# 4. A ViewModel changed after the bundle was written.
cp "$model" "$work/CounterViewModel.cs.orig"
sed -i 's|^    \[RelayCommand\]$|    public string Note { get; set; } = "";\n\n    [RelayCommand]|' "$model"
grep -q 'public string Note' "$model"
echo "== negative case: CounterViewModel gains a Note property after the bundle was written"
set +e
without_node dotnet publish "$project" -c Release -r win-x64 --self-contained false \
  "-p:RunicApplicationFrontendPrebuiltDirectory=$bundle" -o "$work/out-changed" > "$work/logs/publish-changed.log" 2>&1
status=$?
set -e
cp "$work/CounterViewModel.cs.orig" "$model"
error="$(grep -m1 'error RUNICBRIDGE094:' "$work/logs/publish-changed.log" || true)"
if [[ "$status" == 0 || -z "$error" ]]; then
  tail -n 60 "$work/logs/publish-changed.log" >&2
  echo "The publish of a changed ViewModel did not stop with RUNICBRIDGE094 (exit code $status)." >&2
  exit 1
fi
if ! grep -qE "Acme\.App\.CounterViewModel is [0-9A-F]{64} in this build and $bundled in the bundle" <<< "$error"; then
  echo "RUNICBRIDGE094 does not name the View and both fingerprints: $error" >&2
  exit 1
fi
[[ ! -e "$work/out-changed/www" ]] || { echo "The failed publish wrote www/." >&2; exit 1; }
echo "== the publish stops as expected (exit code $status):"
echo "${error#"${error%%[![:space:]]*}"}"

echo "SPLIT_CI_LOCAL_RUN_OK|test|bundle|doctor|publish-win-x64${package_target:+|package-$package_target}|RUNICBRIDGE094"
