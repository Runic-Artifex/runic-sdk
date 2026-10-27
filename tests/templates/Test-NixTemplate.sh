#!/usr/bin/env bash
# Creates `dotnet new runic-app --nix` projects and builds each project's flake in the Nix sandbox. Each built
# application is then served without a window and its page fetched.
#
#   tests/templates/Test-NixTemplate.sh candidates <package-directory> [npm|pnpm|bun|aot ...]
#   tests/templates/Test-NixTemplate.sh published <version> [npm|pnpm|bun|aot ...]
#
# candidates: the packages packed from this checkout (`bun eng/run.mjs pack` or the CI artifact), with the
#   layout of artifacts/packages: nuget/ with the template and NuGet packages, npm/ with the npm archives. The
#   projects are created from that template, whose locks are bound to those archives, and built with this
#   checkout's buildRunicApplication and the SDK's locked nixpkgs. The sandbox's fixed-output downloads cannot
#   reach the candidates in the registries, so:
#   - NuGet: the projects restore the Runic packages from the candidate feed (a NuGet.config above them maps
#     their IDs to it), and each locked candidate is added to the store as the fixed-output path that
#     buildRunicApplication would download, which Nix then does not download.
#   - npm: a loopback registry (template-npm-registry.mjs) serves the candidate archives. The projects' .npmrc
#     points @runic-artifex at it, and the npm project's package-lock.json at its archives
#     (bind-template-candidate-integrities.mjs, which first checks the locks against the archives). On Linux,
#     fixed-output derivations share the host's network, so the sandbox reaches the loopback address.
#   Pull requests and main run this mode, so it tests the code a change makes.
# published: what a user downloads. The template is the published Runic.Application.Templates <version> from
#   nuget.org, its NuGet and npm packages come from the registries, and its flake uses its own runic-sdk input
#   (the v<version> tag) with that tag's locked nixpkgs. The Nix workflow runs this mode after a preview is
#   published, weekly and on request.
#
# "aot" builds the npm project's native-aot package as well. The npm project has the TUnit test project
# (--tests), which both of its packages build and run. With RUNIC_NIX_TEMPLATE_WINDOW=1 each application
# also opens its window under Xvfb (xvfb-run and dbus-run-session from the development shell) with only the
# wrapper's environment, and must have loaded GTK 3 and WebKitGTK 4.1 from the Nix store and started WebKit's
# web process. Run it in the SDK's packaging shell (nix develop .#packaging); it needs network access and Nix
# with flakes.
set -euo pipefail

usage="usage: Test-NixTemplate.sh candidates <package-directory> | published <version> [npm|pnpm|bun|aot ...]"
mode="${1:?$usage}"
source_argument="${2:?$usage}"
shift 2
cases=("$@")
[[ ${#cases[@]} -gt 0 ]] || cases=(npm pnpm bun)

sdk="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d -t runic-nix-template.XXXXXX)"
served=""
registry=""
cleanup() {
  if [[ -n "$served" ]]; then kill -9 -- "-$served" 2>/dev/null || true; fi
  if [[ -n "$registry" ]]; then kill "$registry" 2>/dev/null || true; wait "$registry" 2>/dev/null || true; fi
  rm -rf "$work"
}
trap cleanup EXIT
# The script runs in a consumer shell (nix develop .#packaging, the consumer nixpkgs' .NET SDK), while the
# repository's global.json pins its own SDK without roll-forward. Run the .NET CLI from the work directory, outside
# the checkout, so that pin does not apply; every path passed to it is absolute.
dotnet() { (cd "$work" && command dotnet "$@"); }

export NUGET_PACKAGES="$work/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
registry_url=""
case "$mode" in
  candidates)
    packages="$(cd "$source_argument" && pwd)"
    version="$(jq -r .version "$sdk/eng/workspace.json")"
    template="$packages/nuget/Runic.Application.Templates.$version.nupkg"
    npm_archives=()
    for name in $(jq -r '.npm[].name' "$sdk/eng/workspace.json"); do
      archive="${name#@}"
      npm_archives+=("$packages/npm/${archive/\//-}-$version.tgz")
    done
    for required in "$template" "${npm_archives[@]}"; do
      [[ -f "$required" ]] || { echo "Missing candidate $required; pack with bun eng/run.mjs pack." >&2; exit 1; }
    done
    echo "Using the $version candidates in $packages"
    # Only the candidate feed provides the packages it contains, so a published package of the same version
    # cannot take their place.
    {
      echo '<?xml version="1.0" encoding="utf-8"?>'
      echo '<configuration>'
      echo '  <packageSources>'
      echo '    <clear />'
      echo "    <add key=\"runic-candidate\" value=\"$packages/nuget\" />"
      echo '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
      echo '  </packageSources>'
      echo '  <packageSourceMapping>'
      echo '    <packageSource key="runic-candidate">'
      for package in "$packages"/nuget/*.nupkg; do
        id="$(basename "$package" | sed -E 's/\.[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\.nupkg$//')"
        echo "      <package pattern=\"$id\" />"
      done
      echo '    </packageSource>'
      echo '    <packageSource key="nuget.org">'
      echo '      <package pattern="*" />'
      echo '    </packageSource>'
      echo '  </packageSourceMapping>'
      echo '</configuration>'
    } > "$work/NuGet.config"
    bun "$sdk/tests/templates/template-npm-registry.mjs" "$work/registry.url" "${npm_archives[@]}" &
    registry=$!
    for _ in $(seq 1 100); do
      [[ -s "$work/registry.url" ]] && break
      sleep 0.05
    done
    [[ -s "$work/registry.url" ]] || { echo "The candidate npm registry did not start." >&2; exit 1; }
    registry_url="$(<"$work/registry.url")"
    dotnet new install "$template" --debug:custom-hive "$work/hive" > /dev/null
    nixpkgs="github:NixOS/nixpkgs/$(jq -r .nodes.nixpkgs.locked.rev "$sdk/flake.lock")"
    flake=(--override-input runic-sdk "git+file://$sdk?shallow=1" --override-input nixpkgs "$nixpkgs")
    ;;
  published)
    version="$source_argument"
    echo "Using the published $version template and packages"
    dotnet new install "Runic.Application.Templates::$version" --debug:custom-hive "$work/hive" > /dev/null
    rev="$(nix flake metadata "github:Runic-Artifex/runic-sdk/v$version" --json | jq -r .locks.nodes.nixpkgs.locked.rev)"
    flake=(--override-input nixpkgs "github:NixOS/nixpkgs/$rev")
    ;;
  *)
    echo "$usage" >&2
    exit 2
    ;;
esac
flake+=(--no-write-lock-file)

# Runs a command quietly, and prints its output when it fails.
quiet() {
  "$@" > "$work/step.log" 2>&1 || { cat "$work/step.log" >&2; echo "failed: $*" >&2; return 1; }
}

# Builds a flake output; a fixed-output hash that the project leaves as lib.fakeHash is replaced by the one Nix reports.
build() {
  local project="$1" output="$2" log="$work/build.log"
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

# Candidates: points the project's npm packages at the candidate registry.
use_candidate_registry() {
  local project="$1" manager="$2"
  printf '@runic-artifex:registry=%s/\n' "$registry_url" >> "$project/Frontend/.npmrc"
  if [[ "$manager" == npm ]]; then
    RUNIC_TEMPLATE_NPM_REGISTRY="$registry_url" bun "$sdk/tests/templates/bind-template-candidate-integrities.mjs" \
      "$project/Frontend/package-lock.json" "${npm_archives[@]}"
  fi
}

# Candidates: adds each locked candidate package to the store as the fixed-output path that buildRunicApplication
# fetches it to (nix/lib/nuget-lock-deps.nix: named <id>.<version>.nupkg, with the lock's contentHash).
store_candidates() {
  local project="$1" lock name resolved hash package stored=0
  for lock in "$project/packages.lock.json" "$project/packages.aot.lock.json" "$project"/*/packages.lock.json; do
    [[ -f "$lock" ]] || continue
    while read -r name resolved hash; do
      package="$(find "$packages/nuget" -maxdepth 1 -iname "$name.$resolved.nupkg" | head -n 1)"
      [[ -n "$package" ]] || continue
      [[ "$(nix hash file --type sha512 --base64 "$package")" == "$hash" ]] \
        || { echo "$lock locks $name $resolved with another contentHash than $package" >&2; return 1; }
      nix store add --mode flat --hash-algo sha512 --name "$name.$resolved.nupkg" "$package" > /dev/null
      stored=$((stored + 1))
    done < <(jq -r '.dependencies[] | to_entries[] | select(.value.type != "Project" and .value.contentHash != null)
      | "\(.key) \(.value.resolved) \(.value.contentHash)"' "$lock" | sort -u)
  done
  [[ $stored -gt 0 ]] || { echo "$project locks no candidate package" >&2; return 1; }
  echo "  candidates: $stored locked packages from $packages/nuget"
}

# The build restores in locked mode: a lock without the runtime's section fails at evaluation, and one that no
# longer matches the project fails the restore with NU1004.
locked() {
  local project="$1" log="$work/locked.log"
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
    options=()
    [[ "$manager" == npm ]] && options=(--tests)
    quiet dotnet new runic-app --name NixApp --output "$project" --nix --package-manager "$manager" "${options[@]}" \
      --debug:custom-hive "$work/hive"
    grep -q '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>' "$project/NixApp.csproj"
    grep -qx '/result' "$project/.gitignore"
    if [[ "$manager" == pnpm ]]; then
      # pnpm refuses packages younger than its minimumReleaseAge (a day), which a recent published version is.
      printf 'minimumReleaseAge: 0\n' >> "$project/Frontend/pnpm-workspace.yaml"
    fi
    # What the project's README asks for before the first nix build: the NuGet lock, and the files in Git.
    quiet dotnet restore "$project/NixApp.csproj"
    quiet dotnet restore "$project/NixApp.csproj" -p:PublishAot=true
    if [[ -d "$project/NixApp.Tests" ]]; then
      quiet dotnet restore "$project/NixApp.Tests/NixApp.Tests.csproj"
      jq -e '.dependencies["net10.0/linux-x64"]' "$project/NixApp.Tests/packages.lock.json" > /dev/null \
        || { echo "NixApp.Tests/packages.lock.json has no linux-x64 section" >&2; exit 1; }
    fi
    for lock in packages.lock.json packages.aot.lock.json; do
      jq -e '.dependencies["net10.0/linux-x64"]' "$project/$lock" > /dev/null || { echo "$lock has no linux-x64 section" >&2; exit 1; }
    done
    # The NativeAOT graph adds the compiler package.
    jq -e '.dependencies["net10.0"]["Microsoft.DotNet.ILCompiler"]' "$project/packages.aot.lock.json" > /dev/null \
      || { echo "packages.aot.lock.json does not lock the NativeAOT compiler" >&2; exit 1; }
    if [[ "$mode" == candidates ]]; then
      use_candidate_registry "$project" "$manager"
      store_candidates "$project"
    fi
    git -C "$project" init -q
    git -C "$project" add -A
  fi
  output=default
  [[ "$case" == aot ]] && output=native-aot
  echo "Building $output with $manager"
  build "$project" "$output"
  if [[ -d "$project/NixApp.Tests" ]]; then
    grep -q 'Test run summary: Passed!' "$work/build.log" || { tail -n 60 "$work/build.log" >&2; echo "The build ran no tests." >&2; exit 1; }
    echo "  tests: passed in the build"
  fi
  # The desktop entry and icons are named after the project's ApplicationId.
  test -f "$project/result-$output/share/applications/com.example.NixApp.desktop"
  grep -qx 'Icon=com.example.NixApp' "$project/result-$output/share/applications/com.example.NixApp.desktop"
  test -f "$project/result-$output/share/icons/hicolor/scalable/apps/com.example.NixApp.svg"
  test -f "$project/result-$output/share/icons/hicolor/512x512/apps/com.example.NixApp.png"
  serve "$project/result-$output/bin/NixApp"
  if [[ "${RUNIC_NIX_TEMPLATE_WINDOW:-}" == 1 ]]; then window "$project/result-$output/bin/NixApp"; fi
  echo "  $case: built and served"
  if [[ "$case" == npm ]]; then locked "$project"; fi
done
