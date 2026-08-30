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
