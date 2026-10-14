#!/usr/bin/env bash
# Creates `dotnet new runic-app --nix` projects from this checkout's template and builds each project's flake in
# the Nix sandbox, with this checkout's buildRunicApplication and the SDK's locked nixpkgs. The projects use a
# published Runic version, whose NuGet and npm packages the sandbox's fixed-output fetches can download: the
# template is packed with its locks bound to that version's npm archives. Each built application is then served
# without a window and its page fetched.
#
#   tests/templates/Test-NixTemplate.sh <published-version> [npm|pnpm|bun|aot ...]
#
# "aot" builds the npm project's native-aot package as well. With RUNIC_NIX_TEMPLATE_WINDOW=1 each application
# also opens its window under Xvfb (xvfb-run and dbus-run-session from the development shell) with only the
# wrapper's environment, and must have loaded GTK 3 and WebKitGTK 4.1 from the Nix store and started WebKit's
# web process. Run it in the SDK's development shell (it packs the template with dotnet and Bun); it needs
# network access and Nix with flakes.
set -euo pipefail

version="${1:?usage: Test-NixTemplate.sh <published-version> [npm|pnpm|bun|aot ...]}"
shift
cases=("$@")
[[ ${#cases[@]} -gt 0 ]] || cases=(npm pnpm bun)

sdk="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d -t runic-nix-template.XXXXXX)"
served=""
cleanup() {
  if [[ -n "$served" ]]; then kill -9 -- "-$served" 2>/dev/null || true; fi
  rm -rf "$work"
}
trap cleanup EXIT

workspace_version="$(jq -r .version "$sdk/eng/workspace.json")"
nixpkgs="github:NixOS/nixpkgs/$(jq -r .nodes.nixpkgs.locked.rev "$sdk/flake.lock")"
export NUGET_PACKAGES="$work/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "Packing the template against the published $version packages"
mkdir -p "$work/npm"
for name in $(jq -r '.npm[].name' "$sdk/eng/workspace.json"); do
  archive="${name#@}"
  # stage-template-locks.mjs names the archives by the workspace version and binds the locks to their contents.
  curl -fsSL "https://registry.npmjs.org/$name/-/${name#*/}-$version.tgz" -o "$work/npm/${archive/\//-}-$workspace_version.tgz"
done
dotnet pack "$sdk/tools/Runic.Application.Templates/Runic.Application.Templates.csproj" -c Release -o "$work/template" \
  -p:RunicNuGetTemplateVersion="$version" -p:RunicNpmTemplateVersion="$version" -p:RunicTemplateNpmDirectory="$work/npm" \
  -nologo -v:q
dotnet new install "$work"/template/Runic.Application.Templates.*.nupkg --debug:custom-hive "$work/hive" > /dev/null

# Builds a flake output; a fixed-output hash that the project leaves as lib.fakeHash is replaced by the one Nix reports.
build() {
  local project="$1" output="$2" log="$work/build.log"
  local -a flake=(--override-input runic-sdk "git+file://$sdk?shallow=1" --override-input nixpkgs "$nixpkgs" --no-write-lock-file)
  for _ in 1 2; do
    if nix build "$project#$output" "${flake[@]}" -L --out-link "$project/result-$output" > "$log" 2>&1; then
      return 0
    fi
    local reported
    reported="$(grep -oE 'got: +sha256-[A-Za-z0-9+/=]+' "$log" | head -n 1 | awk '{print $2}' || true)"
    if [[ -n "$reported" ]] && grep -q 'lib.fakeHash' "$project/flake.nix"; then
      echo "  frontend hash: $reported"
      sed -i "s|lib.fakeHash|\"$reported\"|" "$project/flake.nix"
      continue
    fi
    tail -n 60 "$log" >&2
    return 1
  done
  tail -n 60 "$log" >&2
  return 1
}

# Starts the built application serving without a window and fetches its page.
serve() {
  local program="$1" log="$work/serve.log"
  setsid env RUNIC_APPLICATION_SERVE_ONLY=1 "$program" > "$log" 2>&1 < /dev/null &
  served=$!
  local url=""
  for _ in $(seq 1 120); do
    url="$(grep -o 'RUNIC_APPLICATION_URL=[^[:space:]]*' "$log" | head -n 1 | cut -d= -f2- || true)"
    [[ -n "$url" ]] && break
    kill -0 "$served" 2>/dev/null || break
    sleep 0.5
  done
  if [[ -z "$url" ]]; then
    cat "$log" >&2
    return 1
  fi
  curl -fsSL "$url" | grep -q '<script' || { echo "The page at $url has no script." >&2; return 1; }
  kill -9 -- "-$served" 2>/dev/null || true
  served=""
}

# Opens the application's window under Xvfb with a clean environment and checks what it loaded.
window() {
  local program="$1" check="$work/window.sh" log="$work/window.log" runtime="$work/runtime"
  local application
  application="$(dirname "$(dirname "$(readlink -f "$program")")")/lib/"
  cat > "$check" <<EOF
#!/usr/bin/env bash
"$program" > "$log" 2>&1 &
sleep 15
pid=\$(pgrep -n -f "^$application")
grep -qE '/nix/store/[^ ]*-gtk\+3-[^ ]*/lib/libgtk-3\.so' /proc/\$pid/maps || { echo "GTK 3 is not loaded"; exit 1; }
grep -qE '/nix/store/[^ ]*-webkitgtk-[^ ]*/lib/libwebkit2gtk-4\.1\.so' /proc/\$pid/maps || { echo "WebKitGTK 4.1 is not loaded"; exit 1; }
pgrep -f WebKitWebProcess > /dev/null || { echo "WebKit's web process did not start"; exit 1; }
kill -9 \$pid
EOF
  chmod +x "$check"
  mkdir -p "$runtime" "$work/home"
  chmod 700 "$runtime"
  if ! env -i HOME="$work/home" PATH=/run/current-system/sw/bin:/usr/bin:/bin XDG_RUNTIME_DIR="$runtime" \
      "$(command -v dbus-run-session)" -- "$(command -v xvfb-run)" -a "$check"; then
    cat "$log" >&2
    return 1
  fi
}

# The build restores in locked mode: a lock without the runtime's section fails at evaluation, and one that no
# longer matches the project fails the restore with NU1004.
locked() {
  local project="$1" log="$work/locked.log"
  local -a flake=(--override-input runic-sdk "git+file://$sdk?shallow=1" --override-input nixpkgs "$nixpkgs" --no-write-lock-file)
  jq 'del(.dependencies["net10.0/linux-x64"])' "$project/packages.lock.json" > "$work/lock.json"
  cp "$work/lock.json" "$project/packages.lock.json"
  if nix eval "$project#default.drvPath" "${flake[@]}" > "$log" 2>&1 || ! grep -q 'has no linux-x64 section' "$log"; then
    cat "$log" >&2
    return 1
  fi
  git -C "$project" checkout -q -- packages.lock.json
  sed -i 's|</ItemGroup>|  <PackageReference Include="Humanizer.Core" Version="2.14.1" />\n  </ItemGroup>|' "$project/NixApp.csproj"
  if nix build "$project#default" "${flake[@]}" --no-link > "$log" 2>&1 || ! grep -q 'NU1004' "$log"; then
    tail -n 30 "$log" >&2
    return 1
  fi
  git -C "$project" checkout -q -- NixApp.csproj
  echo "  locked mode: a lock without linux-x64 and a stale lock fail"
}

for case in "${cases[@]}"; do
  manager="$case"
  [[ "$case" == aot ]] && manager=npm
  project="$work/NixApp-$manager"
  if [[ ! -d "$project" ]]; then
    echo "Creating a --nix project with $manager"
    dotnet new runic-app --name NixApp --output "$project" --nix --package-manager "$manager" \
      --debug:custom-hive "$work/hive" > /dev/null
    grep -q '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>' "$project/NixApp.csproj"
    grep -qx '/result' "$project/.gitignore"
    if [[ "$manager" == pnpm ]]; then
      # pnpm refuses packages younger than its minimumReleaseAge (a day), which a recent published version is.
      printf 'minimumReleaseAge: 0\n' > "$project/Frontend/pnpm-workspace.yaml"
    fi
    # What the project's README asks for before the first nix build: the NuGet lock, and the files in Git.
    dotnet restore "$project/NixApp.csproj" > /dev/null
    dotnet restore "$project/NixApp.csproj" -p:PublishAot=true > /dev/null
    for lock in packages.lock.json packages.aot.lock.json; do
      jq -e '.dependencies["net10.0/linux-x64"]' "$project/$lock" > /dev/null
    done
    # The NativeAOT graph adds the runtime-specific compiler package, in the linux-x64 section only.
    jq -e '.dependencies["net10.0/linux-x64"]["runtime.linux-x64.Microsoft.DotNet.ILCompiler"]' "$project/packages.aot.lock.json" > /dev/null
    git -C "$project" init -q
    git -C "$project" add -A
  fi
  output=default
  [[ "$case" == aot ]] && output=native-aot
  echo "Building $output with $manager"
  build "$project" "$output"
  test -f "$project/result-$output/share/applications/NixApp.desktop"
  serve "$project/result-$output/bin/NixApp"
  if [[ "${RUNIC_NIX_TEMPLATE_WINDOW:-}" == 1 ]]; then window "$project/result-$output/bin/NixApp"; fi
  echo "  $case: built and served"
  if [[ "$case" == npm ]]; then locked "$project"; fi
done
