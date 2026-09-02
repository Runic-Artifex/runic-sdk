#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <dotnet-runic-executable>" >&2
  exit 2
fi

tool="$1"
probe_root="$(mktemp -d /tmp/runic-tool-parse-presentation.XXXXXXXXXX)"

cleanup() {
  case "$probe_root" in
    /tmp/runic-tool-parse-presentation.*) rm -rf -- "$probe_root" ;;
    *) echo "Refusing to remove unexpected path: $probe_root" >&2 ;;
  esac
}
trap cleanup EXIT

assert_json_failure() {
  local name="$1"
  shift
  local error_file="$probe_root/$name.stderr"
  local output
  local exit_code
  if output="$("$@" 2>"$error_file")"; then
    echo "$name unexpectedly succeeded." >&2
    exit 1
  else
    exit_code=$?
  fi
  if [[ "$exit_code" -ne 2 || -s "$error_file" ]]; then
    echo "$name did not produce an isolated usage JSON response." >&2
    exit 1
  fi
  if [[ "$output" == *"dotnet runic:"* ]]; then
    echo "$name mixed human parse output into JSON transport." >&2
    exit 1
  fi
  node -e '
    const fs = require("node:fs");
    const response = JSON.parse(fs.readFileSync(0, "utf8"));
    if (response.protocol !== "runic.commandline/1" || response.success !== false || response.exitCode !== 2) {
      throw new Error("Expected a canonical usage-error envelope.");
    }
  ' <<< "$output"
}

assert_json_failure "environment-unknown" env RUNIC_COMMANDLINE_OUTPUT=json "$tool" definitely-invalid
assert_json_failure "environment-syntax" env RUNIC_COMMANDLINE_OUTPUT=json "$tool" inspect --artifact
assert_json_failure "transport-before-unknown" "$tool" --output=json definitely-invalid
assert_json_failure "transport-after-unknown" "$tool" definitely-invalid --output=json
assert_json_failure "transport-before-syntax" "$tool" --output=json inspect --artifact
assert_json_failure "transport-after-syntax" "$tool" inspect --artifact --output=json

version_output="$("$tool" --version)"
if [[ ! "$version_output" =~ ^dotnet-runic\ [0-9]+\.[0-9]+\.[0-9]+ ]]; then
  echo "--version did not report the packaged tool version: $version_output" >&2
  exit 1
fi

doctor_help="$probe_root/doctor-help.txt"
"$tool" doctor --help > "$doctor_help"
grep -Fq 'dotnet runic doctor [options]' "$doctor_help"
grep -Fq -- '--configuration <name>' "$doctor_help"
if [[ "$(tail -c 1 "$doctor_help" | od -An -tuC | tr -d ' ')" != "10" ]]; then
  echo "Command help did not end with a newline." >&2
  exit 1
fi

human_failure="$probe_root/human-failure.txt"
if "$tool" doctor --project "$probe_root/missing.csproj" > "$human_failure" 2>&1; then
  echo "doctor unexpectedly accepted a missing project." >&2
  exit 1
fi
if [[ "$(grep -o 'RTKDEV1002' "$human_failure" | wc -l)" -ne 1 ]]; then
  echo "doctor did not present its actionable failure exactly once." >&2
  cat "$human_failure" >&2
  exit 1
fi
if grep -Fq 'RCLI9001' "$human_failure"; then
  echo "doctor leaked the generic execution diagnostic into its actionable failure." >&2
  exit 1
fi
