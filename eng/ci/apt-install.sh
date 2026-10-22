#!/usr/bin/env bash
# Installs Ubuntu packages on a CI runner without letting a slow or stalled mirror
# hold the job (eng/ci/README.md, "Ubuntu packages").
#
# Downloading and installing are separate phases. The download gets a short limit
# per attempt: a healthy mirror delivers the WebKitGTK set (about 90 MB) in seconds,
# while a throttled Azure mirror has needed over 30 minutes. After a failed attempt
# the runner's Azure mirror is dropped in favour of the official Ubuntu archive
# (archive.ubuntu.com, security.ubuntu.com). Both are signed by the same Ubuntu
# archive key; apt still verifies the signed indexes and every package's hash, and
# also checks cached .deb files against them before using them. The install then
# runs without network access. A killed attempt's apt-get or dpkg is stopped and its
# locks released before the next one.
#
# Usage: apt-install.sh [--no-install-recommends] <package>...
# APT_ARCHIVES, if set, is the directory for downloaded .deb files, so a cache can
# restore and save them (.github/actions/apt-install).
set -euo pipefail
dir=$(cd "$(dirname "$0")" && pwd)
options=()
if [ "${1:-}" = --no-install-recommends ]; then options=(--no-install-recommends); shift; fi
packages=("$@")
archives=${APT_ARCHIVES:-/var/cache/apt/archives}
mirror_list=/etc/apt/apt-mirrors.txt
log=$(mktemp)
trap 'rm -f "$log"' EXIT

printf '%s\n' 'Acquire::http::Timeout "30";' 'Acquire::https::Timeout "30";' 'Acquire::Retries "3";' \
  | sudo tee /etc/apt/apt.conf.d/99runic-timeouts >/dev/null
sudo mkdir -p "$archives"
apt=(sudo DEBIAN_FRONTEND=noninteractive apt-get -o "Dir::Cache::Archives=$archives/")
cleanup="sudo pkill -KILL -x apt-get; sudo pkill -KILL -x dpkg; sleep 2; sudo DEBIAN_FRONTEND=noninteractive dpkg --configure -a"

# The x64 runners' sources name a mirror list; the arm64 runners have the file too,
# but their sources name ports.ubuntu.com directly.
uses_mirror_list() {
  grep -rqsF "mirror+file:$mirror_list" /etc/apt/sources.list /etc/apt/sources.list.d
}

# The Ubuntu mirrors apt uses: the runner's mirror list, or the hosts in its sources.
mirrors() {
  if uses_mirror_list; then
    awk '{print $1}' "$mirror_list"
  else
    grep -rhsE '^(deb|URIs:)' /etc/apt/sources.list /etc/apt/sources.list.d | grep -oE 'https?://[^/[:space:]]*ubuntu\.com' || true
  fi | sed -E 's#^https?://##; s#/.*##' | awk '!seen[$0]++' | paste -sd, -
}

# Replaces the runner's Azure mirror with the official Ubuntu archive. Returns 1
# when there is no Azure mirror to replace.
use_official_mirrors() {
  local file changed=1
  for file in "$mirror_list" /etc/apt/sources.list /etc/apt/sources.list.d/*; do
    if [ ! -f "$file" ] || ! grep -q 'azure\.archive\.ubuntu\.com' "$file"; then continue; fi
    if [ "$file" = "$mirror_list" ] && ! uses_mirror_list; then continue; fi
    if [ "$file" = "$mirror_list" ] && grep -v 'azure\.archive\.ubuntu\.com' "$file" | grep -q '^[[:space:]]*[a-z]'; then
      # The list already names the official mirrors after Azure's.
      grep -v 'azure\.archive\.ubuntu\.com' "$file" | sudo tee "$file.runic" >/dev/null
      sudo mv "$file.runic" "$file"
    else
      sudo sed -i 's#://azure\.archive\.ubuntu\.com/#://archive.ubuntu.com/#g' "$file"
    fi
    changed=0
  done
  return "$changed"
}

# Runs apt-get with a time limit, keeping its output for the failure summary.
limited_apt() {
  local seconds=$1
  shift
  "$dir/retry.sh" 1 "$seconds" "${apt[@]}" "$@" 2>&1 | tee -a "$log"
}

# What the failed attempt was doing: apt's errors (apt-get update only warns about
# an index it could not fetch), or the download in progress when the time limit
# stopped it.
failure_summary() {
  local errors
  errors=$(grep -E '^(Err:|E: |W: Failed to fetch)' "$log" | head -n 3 | paste -sd';' - || true)
  if grep -q '::warning::Timed out' "$log"; then
    echo "timed out while fetching $(grep -E '^Get:' "$log" | tail -n 1 | sed -E 's/^Get:[0-9]+ //')${errors:+; $errors}"
  else
    echo "${errors:-apt-get failed without an error line}"
  fi
}

echo "Ubuntu mirrors: $(mirrors); package archives: $archives ($(find "$archives" -maxdepth 1 -name '*.deb' | wc -l) cached)"
limits=(90 240 240)
failures=()
downloaded=false
for ((attempt = 1; attempt <= ${#limits[@]}; attempt++)); do
  used=$(mirrors)
  started=$SECONDS
  : >"$log"
  if limited_apt 120 update && limited_apt "${limits[attempt - 1]}" install --yes --download-only ${options[@]+"${options[@]}"} "${packages[@]}"; then
    downloaded=true
    echo "Downloaded in $((SECONDS - started))s via $used"
    break
  fi
  failures+=("attempt $attempt via $used: $(failure_summary)")
  echo "::warning::Downloading ${packages[*]} failed on attempt $attempt of ${#limits[@]} via $used"
  "$dir/retry.sh" 1 120 bash -c "$cleanup" || true
  if use_official_mirrors; then echo "::warning::Switched to the official Ubuntu mirrors: $(mirrors)"; fi
done
if [ "$downloaded" != true ]; then
  echo "::error title=Ubuntu packages could not be downloaded::${packages[*]} could not be downloaded. $(printf '%s. ' "${failures[@]}")"
  exit 1
fi

export RETRY_CLEANUP="$cleanup"
if ! "$dir/retry.sh" 2 300 "${apt[@]}" install --yes --no-download ${options[@]+"${options[@]}"} "${packages[@]}"; then
  echo "::error title=Ubuntu packages could not be installed::${packages[*]} were downloaded from $(mirrors) but could not be installed; see the apt-get output above."
  exit 1
fi
# A cached archive directory keeps only versions the current indexes still offer,
# so superseded packages do not accumulate in the cache.
if [ -n "${APT_ARCHIVES:-}" ]; then "${apt[@]}" autoclean; fi
echo "Installed ${packages[*]} in ${SECONDS}s"
