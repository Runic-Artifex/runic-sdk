#!/usr/bin/env bash
# Runs `hdiutil "$@"` (create, attach, detach, ...) and retries it while hdiutil reports a transient failure:
# "Resource temporarily unavailable" (EAGAIN) or "Resource busy", which the hosted macOS runners return now and then
# while Disk Arbitration or Spotlight is still busy with another image (I-210, sdk#1018). Other failures are not
# retried. Six attempts, waiting 2, 4, 8, 16 and 30 seconds between them (60 s in all). Before attach is retried, an
# image that an earlier attempt left attached is detached with -force. After the last failure, hdiutil's output and
# `hdiutil info` are printed.
# Usage: hdiutil.sh <verb> <hdiutil arguments...>
set -u
verb=${1:?usage: hdiutil.sh <verb> <arguments...>}
# The image an attach names: its first argument that is a file.
image=""
if [ "$verb" = attach ]; then
  for argument in "${@:2}"; do
    if [ -f "$argument" ]; then image=$(cd "$(dirname "$argument")" && pwd -P)/$(basename "$argument"); break; fi
  done
fi
delays=(2 4 8 16 30)
attempts=$((${#delays[@]} + 1))
for ((attempt = 1; attempt <= attempts; attempt++)); do
  if output=$(hdiutil "$@" 2>&1); then
    printf '%s\n' "$output"
    exit 0
  else
    status=$?
  fi
  printf '%s\n' "$output" >&2
  if [ "$attempt" -eq "$attempts" ] || ! grep -Eq 'Resource temporarily unavailable|Resource busy' <<<"$output"; then
    echo "::error::hdiutil $verb failed with $status (attempt $attempt of $attempts): $(tr '\n' ' ' <<<"$output")"
    echo "hdiutil info:"
    hdiutil info || true
    exit "$status"
  fi
  delay=${delays[$((attempt - 1))]}
  echo "::warning::hdiutil $verb: $(grep -Eo 'Resource temporarily unavailable|Resource busy' <<<"$output" | head -1) (attempt $attempt of $attempts); retrying in ${delay}s"
  sleep "$delay"
  if [ -n "$image" ]; then
    # An attach that failed late can leave the image attached: detach its disk before the next attempt.
    hdiutil info | awk -v image="$image" '
      /^image-path/ { sub(/^image-path *: */, ""); current = $0 }
      /^\/dev\/disk[0-9]+[ \t]/ && current == image { print $1; current = "" }' |
      while read -r device; do
        echo "Detaching $device, which an earlier attempt left attached."
        hdiutil detach -force "$device" || true
      done
  fi
done
