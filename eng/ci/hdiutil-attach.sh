#!/usr/bin/env bash
# Runs `hdiutil attach "$@"` and retries it while hdiutil reports EAGAIN ("Resource temporarily unavailable"), which
# the hosted macOS runners return now and then while Disk Arbitration is still busy with another image (I-210).
# Other failures are not retried. Five attempts, waiting 2, 4, 8 and 16 seconds between them.
# Usage: hdiutil-attach.sh <hdiutil attach arguments...>
set -u
delay=2
for attempt in 1 2 3 4 5; do
  if output=$(hdiutil attach "$@" 2>&1); then
    printf '%s\n' "$output"
    exit 0
  else
    status=$?
  fi
  printf '%s\n' "$output" >&2
  if [ "$attempt" -eq 5 ] || ! grep -q 'Resource temporarily unavailable' <<<"$output"; then exit "$status"; fi
  echo "::warning::hdiutil attach: Resource temporarily unavailable (attempt $attempt of 5); retrying in ${delay}s"
  sleep "$delay"
  delay=$((delay * 2))
done
