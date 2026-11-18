#!/usr/bin/env bash
# Installs Ubuntu packages on a CI runner without letting a slow or stalled mirror
# hold the job (eng/ci/README.md, "Ubuntu packages").
#
# Downloading and installing are separate phases. The download is bounded by
# progress, not by a fixed time per attempt: an attempt is stopped only when the
# package archives (the finished .deb files and apt's partial/ downloads) have not
# grown for APT_STALL_SECONDS (60), and all attempts together share a budget of
# APT_DOWNLOAD_SECONDS (1200). A slow mirror that keeps delivering therefore
# finishes (140 kB/s fetches the Orca set's 113 MB in about 14 minutes), while a
# stalled one fails over within a minute. Downloads resume across attempts.
#
# After a failed attempt the next one puts a different official Ubuntu mirror
# first: the runner's mirror list (/etc/apt/apt-mirrors.txt) is rotated with
# explicit priorities, or the archive host in plain sources is replaced by the next
# of the original host, archive.ubuntu.com and security.ubuntu.com. Only
# *.ubuntu.com hosts are used; all are signed by the same Ubuntu archive key, and
# apt still verifies the signed indexes and every package's hash, including cached
# .deb files before it uses them. The arm64 runners' ports.ubuntu.com has no
# alternative, so they only retry. Each attempt logs the bytes it fetched, its
# average rate and the bytes still to fetch. The install then runs without network
# access. A stopped attempt's apt-get or dpkg is killed and its locks released
# before the next one.
#
# Usage: apt-install.sh [--no-install-recommends] <package>...
# APT_ARCHIVES, if set, is the directory for downloaded .deb files, so a cache can
# restore and save them (.github/actions/apt-install). APT_ETC (default /etc/apt)
# lets eng/ci/contracts.test.mjs run the script against a stubbed apt-get.
set -euo pipefail
dir=$(cd "$(dirname "$0")" && pwd)
options=()
if [ "${1:-}" = --no-install-recommends ]; then options=(--no-install-recommends); shift; fi
packages=("$@")
archives=${APT_ARCHIVES:-/var/cache/apt/archives}
etc=${APT_ETC:-/etc/apt}
mirror_list=$etc/apt-mirrors.txt
stall=${APT_STALL_SECONDS:-60}
budget=${APT_DOWNLOAD_SECONDS:-1200}
poll=${APT_POLL_SECONDS:-2}
tab=$'\t'
max_attempts=5
log=$(mktemp)
originals=$(mktemp -d)
trap 'rm -rf "$log" "$originals"' EXIT

printf '%s\n' 'Acquire::http::Timeout "30";' 'Acquire::https::Timeout "30";' 'Acquire::Retries "3";' \
  | sudo tee "$etc/apt.conf.d/99runic-timeouts" >/dev/null
sudo mkdir -p "$archives"
apt=(sudo DEBIAN_FRONTEND=noninteractive apt-get -o "Dir::Cache::Archives=$archives/")
cleanup="sudo pkill -KILL -x apt-get; sudo pkill -KILL -x dpkg; sleep 2; sudo DEBIAN_FRONTEND=noninteractive dpkg --configure -a"

source_files() {
  local file
  for file in "$etc/sources.list" "$etc"/sources.list.d/*.list "$etc"/sources.list.d/*.sources; do
    if [ -f "$file" ]; then echo "$file"; fi
  done
}

# The x64 runners' sources name a mirror list; the arm64 runners have the file too,
# but their sources name ports.ubuntu.com directly.
uses_mirror_list() {
  local files
  mapfile -t files < <(source_files)
  [ -f "$mirror_list" ] && [ "${#files[@]}" -gt 0 ] && grep -qsF "mirror+file:$mirror_list" "${files[@]}"
}

# The Ubuntu mirrors apt uses, in the order it tries them: the runner's mirror list
# (by priority, then position), or the hosts in its sources.
mirrors() {
  local files
  if uses_mirror_list; then
    awk '/^[[:space:]]*[a-z+]+:\/\// { p = 1e9; for (i = 2; i <= NF; i++) if ($i ~ /^priority:/) p = substr($i, 10) + 0; printf "%012d %06d %s\n", p, NR, $1 }' "$mirror_list" \
      | sort | awk '{print $3}'
  else
    mapfile -t files < <(source_files)
    if [ "${#files[@]}" -gt 0 ]; then
      grep -hsE '^[[:space:]]*(deb|URIs:)' "${files[@]}" | grep -oE 'https?://[^/[:space:]]*ubuntu\.com' || true
    fi
  fi | sed -E 's#^[a-z+]+://##; s#/.*##' | awk '!seen[$0]++' | paste -sd, -
}

# Puts mirror number $1 (counting from 0, wrapping around) of the official Ubuntu
# mirrors first. Returns 1 when there is no other mirror to put first.
prefer_mirror() {
  local turn=$1 entries=() hosts=() entry host uri tags count i file original chosen
  if uses_mirror_list; then
    if [ ! -f "$originals/apt-mirrors.txt" ]; then cp "$mirror_list" "$originals/apt-mirrors.txt"; fi
    # The list's mirrors in apt's order, then the official archive hosts it lacks.
    while IFS= read -r entry; do
      uri=${entry%%[[:space:]]*}
      host=$(sed -E 's#^[a-z+]+://##; s#/.*##' <<<"$uri")
      if [[ $host != *ubuntu.com ]] || [[ " ${hosts[*]} " == *" $host "* ]]; then continue; fi
      hosts+=("$host")
      entries+=("$entry")
    done < <(awk '/^[[:space:]]*[a-z+]+:\/\// { p = 1e9; for (i = 2; i <= NF; i++) if ($i ~ /^priority:/) p = substr($i, 10) + 0; printf "%012d %06d %s\n", p, NR, $0 }' "$originals/apt-mirrors.txt" \
      | sort | cut -d' ' -f3-)
    for host in archive.ubuntu.com security.ubuntu.com; do
      if [[ " ${hosts[*]} " != *" $host "* ]]; then hosts+=("$host"); entries+=("http://$host/ubuntu/"); fi
    done
    count=${#entries[@]}
    if [ "$count" -lt 2 ]; then return 1; fi
    for ((i = 0; i < count; i++)); do
      entry=${entries[(i + turn) % count]}
      uri=${entry%%[[:space:]]*}
      tags=$(tr -s ' \t' '\n' <<<"${entry#"$uri"}" | grep -v -e '^$' -e '^priority:' | paste -sd'\t' - || true)
      printf '%s\tpriority:%d%s\n' "$uri" "$((i + 1))" "${tags:+$tab$tags}"
    done | sudo tee "$mirror_list.runic" >/dev/null
    sudo mv "$mirror_list.runic" "$mirror_list"
  else
    # Plain sources: the archive host of the original sources (an EC2 or Azure mirror,
    # a country mirror or archive.ubuntu.com) and the two official hosts.
    original=$(cat "$originals/host" 2>/dev/null || true)
    if [ -z "$original" ]; then
      original=$(mirrors | tr ',' '\n' | grep -E '(^|\.)archive\.ubuntu\.com$' | head -n 1 || true)
      if [ -z "$original" ]; then return 1; fi
      echo "$original" >"$originals/host"
      while IFS= read -r file; do
        if grep -qF "://$original/" "$file"; then mkdir -p "$originals/files$(dirname "$file")"; cp "$file" "$originals/files$file"; fi
      done < <(source_files)
    fi
    hosts=("$original")
    for host in archive.ubuntu.com security.ubuntu.com; do
      if [ "$host" != "$original" ]; then hosts+=("$host"); fi
    done
    chosen=${hosts[turn % ${#hosts[@]}]}
    while IFS= read -r file; do
      sed "s#://${original//./\\.}/#://$chosen/#g" "$file" | sudo tee "${file#"$originals/files"}" >/dev/null
    done < <(find "$originals/files" -type f)
  fi
}

# Bytes of package archives on disk: finished .deb files and apt's partial downloads,
# which belong to root.
archive_bytes() {
  sudo find "$archives" -maxdepth 2 -type f \( -name '*.deb' -o -path '*/partial/*' \) -printf '%s\n' 2>/dev/null \
    | awk '{ s += $1 } END { printf "%d\n", s }'
}

# Bytes the packages still need: the archives apt would fetch, less what their
# partial downloads already hold. Empty when apt cannot tell.
needed_bytes() {
  local listed partial
  listed=$("$dir/retry.sh" 1 120 "${apt[@]}" install --yes --download-only --print-uris -qq ${options[@]+"${options[@]}"} "${packages[@]}" 2>/dev/null \
    | awk -F"'" 'NF >= 3 { split($3, f, " "); s += f[2] } END { printf "%d\n", s }') || return 0
  partial=$(sudo find "$archives/partial" -maxdepth 1 -type f -printf '%s\n' 2>/dev/null | awk '{ s += $1 } END { printf "%d\n", s }')
  echo $((listed > partial ? listed - partial : 0))
}

# Whether process $1 still runs. kill -0 cannot tell for root's sudo, and an unreaped
# child stays a zombie.
running() {
  local state
  state=$(ps -o stat= -p "$1" 2>/dev/null) && [[ $state != Z* ]]
}

megabytes() { awk -v b="$1" 'BEGIN { printf "%.1f MB", b / 1e6 }'; }
rate() { awk -v b="$1" -v s="$2" 'BEGIN { if (s < 1) s = 1; printf "%.0f kB/s", b / s / 1e3 }'; }

# Stops the download in progress: TERM first, KILL after 15 seconds.
stop_download() {
  local pid=$1 i
  sudo pkill -TERM -x apt-get || true
  for ((i = 0; i < 15; i++)); do
    if ! running "$pid"; then return 0; fi
    sleep 1
  done
  sudo pkill -KILL -x apt-get || true
}

# Runs apt-get --download-only while it makes progress, until the download budget
# ends at second $1. Sets fetched (bytes) and stopped ("", stalled or budget).
download() {
  local deadline=$1 pid status=0 start bytes last changed reported
  start=$(archive_bytes)
  last=$start changed=$SECONDS reported=$SECONDS fetched=0 stopped=''
  "${apt[@]}" install --yes --download-only ${options[@]+"${options[@]}"} "${packages[@]}" > >(tee -a "$log") 2>&1 &
  pid=$!
  while running "$pid"; do
    sleep "$poll"
    bytes=$(archive_bytes)
    if [ "$bytes" -gt "$last" ]; then last=$bytes changed=$SECONDS; fi
    if ((SECONDS - reported >= 60)); then
      echo "Downloading: $(megabytes $((last - start))) in $((SECONDS - begun))s ($(rate $((last - start)) $((SECONDS - begun))))"
      reported=$SECONDS
    fi
    if ! running "$pid"; then break; fi
    if ((SECONDS - changed >= stall)); then
      stopped=stalled
      echo "::warning::No download progress for ${stall}s; stopping apt-get"
      stop_download "$pid"
      break
    fi
    if ((SECONDS >= deadline)); then
      stopped=budget
      echo "::warning::The download budget of ${budget}s is used up; stopping apt-get"
      stop_download "$pid"
      break
    fi
  done
  wait "$pid" || status=$?
  bytes=$(archive_bytes)
  fetched=$((bytes > start ? bytes - start : 0))
  if [ -n "$stopped" ]; then return 1; fi
  return "$status"
}

# What the failed attempt was doing: why it stopped and the download in progress,
# then apt's errors (apt-get update only warns about an index it could not fetch).
failure_summary() {
  local errors
  errors=$(grep -E '^(Err:|E: |W: Failed to fetch)' "$log" | head -n 3 | paste -sd';' - || true)
  case "$stopped" in
    stalled) echo "no progress for ${stall}s while fetching $(grep -E '^Get:' "$log" | tail -n 1 | sed -E 's/^Get:[0-9]+ //')${errors:+; $errors}" ;;
    budget) echo "download budget used up while fetching $(grep -E '^Get:' "$log" | tail -n 1 | sed -E 's/^Get:[0-9]+ //')${errors:+; $errors}" ;;
    *) echo "${errors:-apt-get failed without an error line}" ;;
  esac
}

echo "Ubuntu mirrors: $(mirrors); package archives: $archives ($(find "$archives" -maxdepth 1 -name '*.deb' | wc -l) cached)"
echo "Download limits: stopped after ${stall}s without progress, ${budget}s for all attempts"
failures=()
downloaded=false
deadline=$((SECONDS + budget))
total=0
for ((attempt = 1; attempt <= max_attempts; attempt++)); do
  used=$(mirrors)
  begun=$SECONDS
  stopped='' fetched=0
  : >"$log"
  if "$dir/retry.sh" 1 120 "${apt[@]}" update 2>&1 | tee -a "$log"; then
    need=$(needed_bytes)
    echo "Attempt $attempt of up to $max_attempts via $used: ${need:+$(megabytes "$need") to fetch, }$((deadline - SECONDS))s of the download budget left"
    if download "$deadline"; then
      downloaded=true
      total=$((total + fetched))
      echo "Downloaded $(megabytes "$fetched") in $((SECONDS - begun))s ($(rate "$fetched" $((SECONDS - begun)))) via $used"
      break
    fi
  fi
  total=$((total + fetched))
  left=
  if [ -n "${need:-}" ]; then left=$((need > fetched ? need - fetched : 0)); fi
  summary="fetched $(megabytes "$fetched") in $((SECONDS - begun))s ($(rate "$fetched" $((SECONDS - begun))))${left:+, $(megabytes "$left") still to fetch}"
  failures+=("attempt $attempt via $used: $summary; $(failure_summary)")
  echo "::warning::Downloading ${packages[*]} failed on attempt $attempt via $used: $summary"
  "$dir/retry.sh" 1 120 bash -c "$cleanup" || true
  need=
  if ((deadline - SECONDS < stall)); then break; fi
  if prefer_mirror "$attempt"; then echo "Next attempt prefers $(mirrors | cut -d, -f1): $(mirrors)"; fi
done
if [ "$downloaded" != true ]; then
  echo "::error title=Ubuntu packages could not be downloaded::${packages[*]} could not be downloaded within ${budget}s ($(megabytes "$total") fetched). $(printf '%s. ' "${failures[@]}")"
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
