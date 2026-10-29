#!/usr/bin/env bash
# Builds the experimental native Desktop server (native/runic-server, X1) for one .NET runtime identifier and copies
# its shared library (JIT applications) and static library (NativeAOT) to artifacts/native/runic-server/<rid>/, where
# the Runic.Desktop.NativeServer package and the repository's -p:RunicDesktopServer=Native builds read them.
#
#   eng/native/build-runic-server.sh [rid]          # the host's RID when omitted
#
# RUNIC_CARGO_BUILD replaces "cargo build", for example "cargo zigbuild" to link another Linux architecture or musl;
# with it, RUNIC_GLIBC (CI: 2.28, as old as .NET 10's oldest glibc distribution) sets the glibc a linux-x64 or
# linux-arm64 library needs at least. Cross targets need their Rust standard library (rustup target add <triple>); the
# Nix shell provides the host's. A library built in the Nix shell has a /nix/store RUNPATH: use it locally only.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
host="$(rustc -vV | sed -n 's/^host: //p')"

host_rid() {
  case "$host" in
    x86_64-unknown-linux-gnu) echo linux-x64 ;;
    aarch64-unknown-linux-gnu) echo linux-arm64 ;;
    x86_64-unknown-linux-musl) echo linux-musl-x64 ;;
    aarch64-unknown-linux-musl) echo linux-musl-arm64 ;;
    x86_64-pc-windows-msvc) echo win-x64 ;;
    aarch64-pc-windows-msvc) echo win-arm64 ;;
    aarch64-apple-darwin) echo osx-arm64 ;;
    x86_64-apple-darwin) echo osx-x64 ;;
    *) echo "unsupported host $host" >&2; exit 2 ;;
  esac
}

rid="${1:-$(host_rid)}"
case "$rid" in
  linux-x64) triple=x86_64-unknown-linux-gnu ;;
  linux-arm64) triple=aarch64-unknown-linux-gnu ;;
  linux-musl-x64) triple=x86_64-unknown-linux-musl ;;
  linux-musl-arm64) triple=aarch64-unknown-linux-musl ;;
  win-x64) triple=x86_64-pc-windows-msvc ;;
  win-arm64) triple=aarch64-pc-windows-msvc ;;
  osx-arm64) triple=aarch64-apple-darwin ;;
  osx-x64) triple=x86_64-apple-darwin ;;
  *) echo "unsupported runtime identifier $rid" >&2; exit 2 ;;
esac

# .NET on musl links musl dynamically, so the library must too (Rust's musl targets default to a static C runtime,
# which cannot build a cdylib).
if [[ "$triple" == *-musl ]]; then
  export RUSTFLAGS="${RUSTFLAGS:-} -C target-feature=-crt-static"
fi
# Reproducible paths in panic messages and debug info.
export RUSTFLAGS="${RUSTFLAGS:-} --remap-path-prefix=$root=/runic-sdk --remap-path-prefix=${CARGO_HOME:-$HOME/.cargo}=/cargo"
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$root" log -1 --format=%ct 2>/dev/null || echo 0)}"

read -r -a build <<<"${RUNIC_CARGO_BUILD:-cargo build}"
if [[ "$triple" == "$host" && -z "${RUNIC_CARGO_BUILD:-}" ]]; then
  "${build[@]}" --manifest-path "$root/native/Cargo.toml" --locked --release -p runic-server
  output="$root/native/target/release"
else
  target="$triple"
  if [[ "$triple" == *-linux-gnu && -n "${RUNIC_GLIBC:-}" ]]; then
    target="$triple.$RUNIC_GLIBC"
  fi
  "${build[@]}" --manifest-path "$root/native/Cargo.toml" --locked --release -p runic-server --target "$target"
  output="$root/native/target/$triple/release"
fi

destination="$root/artifacts/native/runic-server/$rid"
rm -rf "$destination"
mkdir -p "$destination"
case "$rid" in
  win-*) cp "$output/runic_server.dll" "$output/runic_server.lib" "$destination/" ;;
  osx-*) cp "$output/librunic_server.dylib" "$output/librunic_server.a" "$destination/" ;;
  *) cp "$output/librunic_server.so" "$output/librunic_server.a" "$destination/" ;;
esac
ls -l "$destination"
