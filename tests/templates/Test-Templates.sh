#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "Usage: $0 <package-version> <package-directory> <runic-npm-archive.tgz>..." >&2
  exit 2
fi

package_version="$1"
package_directory="$(cd "$2" && pwd)"
npm_archives=()
for archive in "${@:3}"; do npm_archives+=("$(realpath "$archive")"); done
script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_directory/../.." && pwd)"
template_package="$package_directory/Runic.Application.Templates.$package_version.nupkg"
template_tmp="$(mktemp -d /tmp/runic-views-templates.XXXXXXXXXX)"
registry_pid=""
# The process that keeps the shared virtual display open, if one was started.
display_holder_pid=""
display_holder_child=""
# Process group of a running serve-only application or dotnet runic dev.
served_group=""

# Processes in a group, without the Roslyn and MSBuild build servers that a
# build starts and that outlive it by design.
started_processes() {
  ps -o pid=,args= -g "$1" | grep -vE 'VBCSCompiler|MSBuild\.dll.*/nodemode:' || true
}

stop_served() {
  if [[ -n "$served_group" ]]; then
    kill -TERM -- "-$served_group" 2>/dev/null || true
    wait "$served_group" 2>/dev/null || true
    served_group=""
  fi
}

cleanup() {
  stop_served
  if [[ -n "$display_holder_pid" ]]; then
    # Ending the holder lets xvfb-run stop Xvfb; ending xvfb-run itself would leave Xvfb running.
    if [[ -n "$display_holder_child" ]]; then
      kill "$display_holder_child" 2>/dev/null || true
    else
      kill "$display_holder_pid" 2>/dev/null || true
    fi
    wait "$display_holder_pid" 2>/dev/null || true
  fi
  if [[ -n "$registry_pid" ]]; then
    kill "$registry_pid" 2>/dev/null || true
    wait "$registry_pid" 2>/dev/null || true
  fi
  case "$template_tmp" in
    /tmp/runic-views-templates.*) rm -rf -- "$template_tmp" ;;
    *) echo "Refusing to remove unexpected path: $template_tmp" >&2 ;;
  esac
}
trap cleanup EXIT

for required in "$template_package" "${npm_archives[@]}"; do
  if [[ ! -f "$required" ]]; then
    echo "Required template acceptance input is missing: $required" >&2
    exit 1
  fi
done

# On Linux, WebUI loads GTK 3 and WebKitGTK 4.1 when it creates a CS-WebUI
# window, if both are installed, and calls gtk_init, which ends the process
# when no display opens, also with RUNIC_APPLICATION_SERVE_ONLY. Without a
# display, such as in the CI lane that installs WebKitGTK for the Desktop
# smoke, every application therefore shares one virtual display.
if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]] && command -v xvfb-run > /dev/null; then
  display_file="$template_tmp/virtual-display"
  # The holder prints its PID; ending it lets xvfb-run stop Xvfb and remove its authority file.
  xvfb-run -a bash -c 'printf "%s %s %s\n" "$$" "$DISPLAY" "$XAUTHORITY" > "$1"; exec sleep infinity' _ "$display_file" &
  display_holder_pid=$!
  for _ in $(seq 1 300); do
    [[ -s "$display_file" ]] && break
    sleep 0.1
  done
  [[ -s "$display_file" ]]
  read -r display_holder_child DISPLAY XAUTHORITY < "$display_file"
  export DISPLAY XAUTHORITY
fi

export DOTNET_CLI_HOME="$template_tmp/dotnet-home"
export NUGET_PACKAGES="$template_tmp/nuget"
export BUN_INSTALL_CACHE_DIR="$template_tmp/bun-cache"
export PNPM_CONFIG_STORE_DIR="$template_tmp/pnpm-store"
restore_sources=(--source "$package_directory" --source https://api.nuget.org/v3/index.json)
tool_restore_options=()
tool_source_options=(--add-source "$package_directory")
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  restore_sources=(--configfile "$NUGET_CONFIG_FILE")
  tool_restore_options=(--configfile "$NUGET_CONFIG_FILE")
  tool_source_options=()
elif [[ -n "${RUNIC_VERIFICATION_FEED:-}" ]]; then
  restore_sources=(--source "$RUNIC_VERIFICATION_FEED" "${restore_sources[@]}")
fi

# The documented commands take no source options. A NuGet.config above the
# generated projects supplies the candidate feed to dotnet tool restore,
# dotnet runic dev, and dotnet build exactly as a user's configuration would.
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  cp -- "$NUGET_CONFIG_FILE" "$template_tmp/NuGet.config"
else
  {
    echo '<?xml version="1.0" encoding="utf-8"?>'
    echo '<configuration>'
    echo '  <packageSources>'
    echo '    <clear />'
    if [[ -n "${RUNIC_VERIFICATION_FEED:-}" && "$(realpath "$RUNIC_VERIFICATION_FEED")" != "$package_directory" ]]; then
      echo "    <add key=\"runic-verification\" value=\"$RUNIC_VERIFICATION_FEED\" />"
    fi
    echo "    <add key=\"runic-candidate\" value=\"$package_directory\" />"
    echo '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
    echo '  </packageSources>'
    echo '</configuration>'
  } > "$template_tmp/NuGet.config"
fi

dotnet new install "$template_package" --force
# --host cswebui references the preview Runic.Application.Views.CsWebUi package, which ships on its own
# prerelease version (eng/release/README.md, "Package tiers"); the template pack stamps that version separately.
# The template has no version options: the candidate template is packed with the versions it references
# (-p:RunicNuGetTemplateVersion=..., -p:RunicCsWebUiNuGetTemplateVersion=..., -p:RunicNpmTemplateVersion=...),
# and each cswebui variant restores the stamped version from the candidate feed.
cswebui_packages=("$package_directory"/Runic.Application.Views.CsWebUi.*.nupkg)
if [[ ${#cswebui_packages[@]} -ne 1 || ! -f "${cswebui_packages[0]}" ]]; then
  echo "Expected one Runic.Application.Views.CsWebUi package in $package_directory." >&2
  exit 1
fi
cswebui_version="$(basename "${cswebui_packages[0]}" .nupkg)"
cswebui_version="${cswebui_version#Runic.Application.Views.CsWebUi.}"
tool_directory="$template_tmp/tools"
dotnet tool install dotnet-runic \
  --tool-path "$tool_directory" \
  --version "$package_version" \
  "${tool_source_options[@]}" \
  "${tool_restore_options[@]}"

pnpm_version="$(bun "$repository_root/eng/toolchain.mjs" pnpm)"
bun_version="$(bun "$repository_root/eng/toolchain.mjs" bun)"
package_manager_directory="$template_tmp/package-managers"
npm install --global --prefix "$package_manager_directory" "pnpm@$pnpm_version" \
  --allow-scripts=pnpm --no-audit --no-fund
export PATH="$package_manager_directory/bin:$PATH"
[[ "$(cd "$template_tmp" && pnpm --version)" == "$pnpm_version" ]]
[[ "$(bun --version)" == "$bun_version" ]]

npm_archive_version() {
  bun -e '
    const { execFileSync } = require("node:child_process");
    const manifest = JSON.parse(execFileSync("tar", ["-xOf", process.argv[1], "package/package.json"], { encoding: "utf8" }));
    process.stdout.write(manifest.version);
  ' "$1"
}

# Runic npm packages share one release-train version.
views_npm_version="$(npm_archive_version "${npm_archives[0]}")"
registry_ready="$template_tmp/template-npm-registry.url"
bun "$script_directory/template-npm-registry.mjs" \
  "$registry_ready" "${npm_archives[@]}" &
registry_pid=$!
for _ in $(seq 1 100); do
  [[ -s "$registry_ready" ]] && break
  sleep 0.05
done
[[ -s "$registry_ready" ]]
registry_url="$(<"$registry_ready")"

# Every template installs the Views runtime and its framework binding.
configure_candidate_registry() {
  local manager="$1"
  local output="$2"
  (cd "$output/Frontend" && npm config set --location=project @runic-artifex:registry "$registry_url")
  if [[ "$manager" == npm ]]; then
    RUNIC_TEMPLATE_NPM_REGISTRY="$registry_url" bun "$script_directory/bind-template-candidate-integrities.mjs" \
      "$output/Frontend/package-lock.json" "${npm_archives[@]}"
  fi
}

frontend_script() {
  local manager="$1"
  local frontend="$2"
  local script="$3"
  case "$manager" in
    npm) (cd "$frontend" && npm run "$script") ;;
    pnpm) (cd "$frontend" && pnpm run "$script") ;;
    bun) (cd "$frontend" && bun run --bun "$script") ;;
  esac
}

# Starts a command in its own process group with RUNIC_APPLICATION_SERVE_ONLY,
# waits for the application's URL, and fetches the served document. The
# development document points at the frontend development server; a production
# document does not.
serve_and_fetch() {
  local directory="$1"
  local log="$2"
  local document="$3"
  local expect_development="$4"
  shift 4
  setsid env -C "$directory" RUNIC_APPLICATION_SERVE_ONLY=1 "$@" > "$log" 2>&1 < /dev/null &
  served_group=$!
  local url=""
  for _ in $(seq 1 600); do
    url="$(grep -o 'RUNIC_APPLICATION_URL=[^[:space:]]*' "$log" | head -n 1 | cut -d= -f2- || true)"
    [[ -n "$url" ]] && break
    if ! kill -0 "$served_group" 2>/dev/null; then break; fi
    sleep 0.5
  done
  if [[ -z "$url" ]]; then
    echo "The application did not report its URL: $*" >&2
    tail -n 80 "$log" >&2
    exit 1
  fi
  # WebUI admits one client and identifies it by the cookie set on the first
  # response, so later requests must send it back.
  local cookies="$document.cookies"
  curl -fsS -c "$cookies" -b "$cookies" "$url/index.html" > "$document"
  curl -fsS -c "$cookies" -b "$cookies" "$url/runic-cswebui.js" > /dev/null
  curl -fsS -c "$cookies" -b "$cookies" "$url/runic-views.js" > "$document.runic-views.js"
  grep -Fq 'CS-WebUI owns the browser transport' "$document.runic-views.js"
  # A second window's page is served from the same web root as index.html.
  local pages=("$document")
  if [[ -f "$directory/Frontend/settings.html" ]]; then
    curl -fsS -c "$cookies" -b "$cookies" "$url/settings.html" > "$document.settings.html"
    grep -Fq '<title>Second window</title>' "$document.settings.html"
    pages+=("$document.settings.html")
  fi
  if [[ "$expect_development" == true ]]; then
    local page
    for page in "${pages[@]}"; do grep -Fq 'http://127.0.0.1:' "$page"; done
    # An IDE or terminal may end only dotnet runic dev; it must stop the
    # frontend and native host processes it started.
    kill -TERM "$served_group"
    for _ in $(seq 1 60); do
      [[ -z "$(started_processes "$served_group")" ]] && break
      sleep 0.5
    done
    if [[ -n "$(started_processes "$served_group")" ]]; then
      echo "dotnet runic dev left processes running after SIGTERM:" >&2
      started_processes "$served_group" >&2
      exit 1
    fi
  else
    if grep -Fq 'http://127.0.0.1:' "${pages[@]}"; then
      echo "The production build served a development document." >&2
      exit 1
    fi
  fi
  stop_served
}

verify_template() {
  local framework="$1"
  local manager="$2"
  local host="$3"
  local view_models="$4"
  # Trailing flags: "defaults" omits --host and --view-models, so the checks
  # below prove that the template's defaults are this host and ViewModel
  # library; "tests" adds the TUnit test project and runs it; "gtk4" adds
  # --linux-gtk 4 to the desktop host.
  local defaults="" tests="" restore="" linux_gtk=3 flag
  for flag in "${@:5}"; do
    case "$flag" in
      defaults) defaults=defaults ;;
      tests) tests=tests ;;
      restore) restore=restore ;;
      gtk4) linux_gtk=4 ;;
      *) echo "Unknown verify_template flag: $flag" >&2; exit 2 ;;
    esac
  done
  if [[ "$linux_gtk" == 4 && ( "$host" != desktop || "$defaults" == defaults ) ]]; then
    echo "The gtk4 flag needs an explicit desktop host." >&2
    exit 2
  fi
  local selection=(--host "$host" --view-models "$view_models")
  [[ "$linux_gtk" == 4 ]] && selection+=(--linux-gtk 4)
  [[ "$defaults" == defaults ]] && selection=()
  local tests_options=()
  [[ "$tests" == tests ]] && tests_options=(--tests)
  [[ "$restore" == restore ]] && tests_options+=(--restore)
  # SvelteKit renders its page from src/app.html and its routes; the other Vite frontends build HTML inputs.
  local html_inputs=true
  [[ "$framework" == angular || "$framework" == sveltekit ]] && html_inputs=false
  # --host desktop --linux-gtk 4 names the project ...DesktopGtk4... and its directory ...-desktop-gtk4-....
  local host_label="$host" host_name="${host^}"
  if [[ "$linux_gtk" == 4 ]]; then host_label+=-gtk4; host_name+=Gtk4; fi
  local project_name="Acceptance${framework^}${manager^}${host_name}${view_models^}"
  local output="$template_tmp/$framework-$manager-$host_label-$view_models"
  local expected_manager_version
  local selected_lock
  case "$manager" in
    npm) expected_manager_version="$(npm --version)"; selected_lock=package-lock.json ;;
    pnpm) expected_manager_version="$pnpm_version"; selected_lock=pnpm-lock.yaml ;;
    bun) expected_manager_version="$bun_version"; selected_lock=bun.lock ;;
  esac

  dotnet new runic-app \
    --name "$project_name" \
    --output "$output" \
    --frontend "$framework" \
    --package-manager "$manager" \
    "${selection[@]}" \
    "${tests_options[@]}"

  # The opt-in restore post-action restores the created projects, which writes their NuGet lock files (SEC-31).
  if [[ "$restore" == restore ]]; then
    test -f "$output/obj/project.assets.json"
    test -f "$output/packages.lock.json"
    [[ "$tests" != tests ]] || test -f "$output/$project_name.Tests/packages.lock.json"
  elif [[ -e "$output/obj" ]]; then
    echo "The $framework project without --restore was restored." >&2
    exit 1
  fi
  grep -Fq '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>' "$output/$project_name.csproj"

  # The candidate template references the candidate packages: the pack stamped their versions.
  if ! grep -Fq "\"version\": \"$package_version\"" "$output/.config/dotnet-tools.json" \
    || grep -E 'Include="Runic\.[^"]*" Version=' "$output/$project_name.csproj" \
      | grep -vF "Version=\"$package_version\"" | grep -vF "Include=\"Runic.Application.Views.CsWebUi\" Version=\"$cswebui_version\""; then
    echo "The template does not reference the Runic $package_version packages; pack it with -p:RunicNuGetTemplateVersion=$package_version." >&2
    exit 1
  fi
  if ! grep -Fq "\"@runic-artifex/views\": \"$views_npm_version\"" "$output/Frontend/package.json"; then
    echo "The template does not reference the Runic $views_npm_version npm packages; pack it with -p:RunicNpmTemplateVersion=$views_npm_version." >&2
    exit 1
  fi

  test -f "$output/Frontend/$selected_lock"
  for other_lock in package-lock.json pnpm-lock.yaml bun.lock; do
    [[ "$other_lock" == "$selected_lock" ]] || test ! -f "$output/Frontend/$other_lock"
  done
  grep -Fq "\"packageManager\": \"$manager@$expected_manager_version\"" "$output/Frontend/package.json"
  # The frontend package is named after the project, not the template.
  local package_name
  package_name="$(sed -E -e 's/([a-z0-9])([A-Z])/\1-\2/g' -e 's/([a-zA-Z])([0-9])/\1-\2/g' <<< "$project_name" | tr '[:upper:]' '[:lower:]')"
  grep -Fq "\"name\": \"$package_name\"" "$output/Frontend/package.json"
  grep -Fq 'RunicApplicationFrontendWindowProject>true' "$output/$project_name.csproj"
  # A Svelte config keeps vite-plugin-svelte from reporting a missing config on every build.
  [[ "$framework" != svelte ]] || grep -Fq 'vitePreprocess()' "$output/Frontend/svelte.config.js"
  # One Window declaration and one application body serve every host and MVVM library.
  grep -Fq 'public sealed partial class WorkspaceWindow : RunicWindow<WorkspaceViewModel>;' "$output/Views.cs"
  if grep -nE 'Views\.(Desktop|CsWebUi)|(Desktop|CsWebUi)BridgeWindow|CsWebUiWindow' "$output/Views.cs"; then
    echo "The $host Window declaration depends on its host." >&2
    exit 1
  fi
  grep -Fq 'await using var window = await host.OpenWindowAsync<WorkspaceWindow>(' "$output/Program.cs"
  grep -Fq 'await window.WaitForCloseAsync();' "$output/Program.cs"
  local index_html="$output/Frontend/index.html"
  [[ "$framework" == angular ]] && index_html="$output/Frontend/src/index.html"
  [[ "$framework" == sveltekit ]] && index_html="$output/Frontend/src/app.html"
  # The page names no host: every host serves its Views client as runic-views.js (#237).
  grep -Fq '<script src="runic-views.js"></script>' "$index_html"
  if grep -nE 'runic-(cswebui|desktop-views|desktop)\.js' "$index_html"; then
    echo "The $host frontend names a host script." >&2
    exit 1
  fi
  if [[ "$framework" == sveltekit ]]; then
    grep -Fq 'runicAdapter({ out: "dist" })' "$output/Frontend/vite.config.ts"
    grep -Fq "\"@runic-artifex/sveltekit\": \"$views_npm_version\"" "$output/Frontend/package.json"
  fi
  if [[ "$framework" != angular ]]; then
    [[ "$framework" == sveltekit ]] || grep -Fq 'runic()' "$output/Frontend/vite.config.ts"
    if grep -n 'desktop' "$output/Frontend/vite.config.ts"; then
      echo "The $host Vite configuration chooses a host." >&2
      exit 1
    fi
  fi
  local host_client
  case "$host" in
    cswebui)
      host_client=runic-cswebui.js
      grep -Fq "<PackageReference Include=\"Runic.Application.Views.CsWebUi\" Version=\"$cswebui_version\" />" "$output/$project_name.csproj"
      grep -Fq 'return RunicCsWebUiHost.Run(provider, RunAsync);' "$output/Program.cs"
      ;;
    desktop)
      host_client=runic-desktop-views.js
      grep -Fq 'Runic.Application.Views.Desktop' "$output/$project_name.csproj"
      grep -Fq 'return RunicDesktopHost.Run(provider, options, RunAsync);' "$output/Program.cs"
      grep -Fq 'DiagnosticSink = ReportDiagnostic' "$output/Program.cs"
      ;;
  esac
  # Every host registers the platform-services facade, and no project picks a provider package itself.
  grep -Fq 'services.AddRunicPlatformServices();' "$output/WorkspaceServices.cs"
  if grep -nE 'NativeOwner|PlatformProvider|Runic\.Platform\.' "$output/Views.cs" "$output/Program.cs" "$output/WorkspaceServices.cs" "$output/$project_name.csproj"; then
    echo "The $host project chooses a platform provider instead of using AddRunicPlatformServices." >&2
    exit 1
  fi
  # Only the GTK 4 variant selects GTK 4 and references its desktop package.
  if [[ "$linux_gtk" == 4 ]]; then
    grep -Fq 'DiagnosticSink = ReportDiagnostic }.WithGtk4(ApplicationIdentity.Id);' "$output/Program.cs"
    grep -Fq '"Runic.Desktop.Gtk4"' "$output/$project_name.csproj"
  elif grep -nE 'Gtk4|Linux\.Portal' "$output/Program.cs" "$output/$project_name.csproj"; then
    echo "The $host project references the GTK 4 profile." >&2
    exit 1
  fi
  case "$view_models" in
    toolkit) grep -Fq 'CommunityToolkit.Mvvm' "$output/$project_name.csproj" ;;
    reactiveui) grep -Fq 'Runic.Application.Views.ReactiveUI' "$output/$project_name.csproj" ;;
  esac
  local tests_project="$output/$project_name.Tests/$project_name.Tests.csproj"
  if [[ "$tests" == tests ]]; then
    # The template pins the repository's TUnit version, and dotnet test needs
    # Microsoft.Testing.Platform mode on the .NET 10 SDK.
    local tunit_version
    tunit_version="$(grep -o 'Include="TUnit" Version="[^"]*"' "$repository_root/Directory.Packages.props" | cut -d'"' -f4)"
    [[ -n "$tunit_version" ]]
    grep -Fq "<PackageReference Include=\"TUnit\" Version=\"$tunit_version\" />" "$tests_project"
    grep -Fq 'AdditionalProperties="RunicApplicationFrontendBuildEnabled=false"' "$tests_project"
    grep -Fq '"runner": "Microsoft.Testing.Platform"' "$output/global.json"
    grep -Fq "$project_name.Tests/**" "$output/$project_name.csproj"
  elif [[ -e "$output/$project_name.Tests" || -e "$output/global.json" ]] \
    || grep -Fq '.Tests/**' "$output/$project_name.csproj"; then
    echo "The $framework project without --tests contains the test project." >&2
    exit 1
  fi
  if grep -rnIE --exclude='*lock*' '^[[:space:]]*#(if|elif|else|endif)\b|<!--#|__[A-Z][A-Z_]+__' "$output"; then
    echo "The generated $framework project contains unprocessed template syntax." >&2
    exit 1
  fi
  # The Window project switch is the one Runic build setting the template declares.
  if grep -vF 'RunicApplicationFrontendWindowProject>' "$output/$project_name.csproj" | grep -Eq 'RunicApplicationFrontend|RunicBridge|RunicViewsWindowProject'; then
    echo "The generated $framework project declares settings the Runic packages default." >&2
    exit 1
  fi
  if grep -rniIE 'Runic\.Application\.Bridge|application-bridge|@runic-artifex/(views-angular|views-svelte|desktop)' "$output"; then
    echo "The generated $framework template contains a removed Bridge or Desktop package." >&2
    exit 1
  fi
  if [[ "$host" == cswebui ]] && grep -rniI 'Runic\.Desktop' "$output"; then
    echo "The generated CS-WebUI project references Runic Desktop." >&2
    exit 1
  fi
  configure_candidate_registry "$manager" "$output"
  # A second window gets its own page as a second Vite build input, the step the
  # template's vite.config.ts describes. Angular builds through the Angular CLI
  # and keeps a single page here.
  if [[ "$html_inputs" == true ]]; then
    sed -i 's|input: \["index.html"\]|input: ["index.html", "settings.html"]|' \
      "$output/Frontend/vite.config.ts"
    grep -Fq 'input: ["index.html", "settings.html"]' "$output/Frontend/vite.config.ts"
  fi

  # The documented first run, verbatim: dotnet tool restore, then
  # dotnet runic dev. doctor must already work before anything is restored.
  (cd "$output" && dotnet tool restore)
  if ! (cd "$output" && dotnet runic doctor) > "$output/doctor-unrestored.txt"; then
    cat "$output/doctor-unrestored.txt" >&2
    exit 1
  fi
  # The restore post-action already restored a --restore project.
  if [[ "$restore" == restore ]]; then
    grep -Fq 'PASS compatibility-set:' "$output/doctor-unrestored.txt"
  else
    grep -Fq 'WARN compatibility-set: The project has not been restored yet' "$output/doctor-unrestored.txt"
  fi
  # The browser check follows the host, before restore too; Desktop projects
  # get no CS-WebUI advice.
  if [[ "$host" == desktop* ]]; then
    grep -Fq 'PASS browser: Not required' "$output/doctor-unrestored.txt"
    if grep -Fq 'CS-WebUI' "$output/doctor-unrestored.txt"; then
      cat "$output/doctor-unrestored.txt" >&2
      exit 1
    fi
  fi
  if [[ "$html_inputs" == true ]]; then
    sed 's|<title>[^<]*</title>|<title>Second window</title>|' "$output/Frontend/index.html" > "$output/Frontend/settings.html"
  fi
  # serve_and_fetch also fetches CS-WebUI's runic-cswebui.js. Desktop variants
  # open a native window; the smoke lane opens them under Xvfb (run_desktop_smoke).
  if [[ "$manager" == npm && "$host" == cswebui ]]; then
    serve_and_fetch "$output" "$output/dotnet-runic-dev.log" "$output/dev-document.html" true \
      dotnet runic dev
    # Every starter's development server confirms the dev session (runic(), runicDevSession() for SvelteKit,
    # runicDevSessionProxy() in the Angular proxyConfig), so dev never takes another process on its port for it.
    if ! grep -Fq 'development server confirmed this dev session.' "$output/dotnet-runic-dev.log"; then
      echo "The $framework development server did not confirm the dev session." >&2
      tail -n 80 "$output/dotnet-runic-dev.log" >&2
      exit 1
    fi
  fi
  if [[ "$manager" == npm && "$host" == cswebui && "$html_inputs" == true ]]; then
    # A window entry that is a build input but has no file fails before the
    # build, naming the page instead of timing out the window's connection.
    mv "$output/Frontend/settings.html" "$output/settings.html.kept"
    if (cd "$output" && RUNIC_APPLICATION_SERVE_ONLY=1 timeout -k 10 300 dotnet runic dev --no-restore) \
      > "$output/dotnet-runic-dev-missing.log" 2>&1 < /dev/null; then
      echo "dotnet runic dev accepted a missing window entry." >&2
      exit 1
    fi
    if ! grep -Fq "RAPPDEV1009: Window entry not found: 'settings.html'" "$output/dotnet-runic-dev-missing.log"; then
      tail -n 80 "$output/dotnet-runic-dev-missing.log" >&2
      exit 1
    fi
    mv "$output/settings.html.kept" "$output/Frontend/settings.html"
  fi

  dotnet restore "$output/$project_name.csproj" "${restore_sources[@]}"
  test -f "$output/packages.lock.json"
  # The runic-view item template adds a View and a Window with their ViewModels to the restored project, in its
  # RootNamespace; the build generates their clients.
  local root_namespace
  root_namespace="$(sed -n 's|^namespace \(.*\);$|\1|p' "$output/Views.cs")"
  (cd "$output" && dotnet new runic-view --name Settings --view-models "$view_models" \
    && dotnet new runic-view --name Preferences --window --view-models "$view_models") > "$output/runic-view.log"
  grep -Fq "namespace $root_namespace;" "$output/SettingsView.cs"
  grep -Fq "public sealed partial class PreferencesWindow : RunicWindow<PreferencesViewModel>;" "$output/PreferencesWindow.cs"
  grep -Fq 'services.AddScoped<NameViewModel>()' "$output/runic-view.log"
  "$tool_directory/dotnet-runic" dev \
    --project "$output/$project_name.csproj" \
    --dry-run > "$output/dotnet-runic-dev-plan.txt"
  grep -Fq 'Runic Views Window project' "$output/dotnet-runic-dev-plan.txt"

  if ! (cd "$output" && dotnet runic doctor) > "$output/doctor.txt"; then
    cat "$output/doctor.txt" >&2
    exit 1
  fi
  grep -Fq "PASS package-manager: $manager $expected_manager_version matches certified baseline" "$output/doctor.txt"
  grep -Fq "PASS compatibility-set:" "$output/doctor.txt"
  if [[ "$host" == desktop* ]]; then
    grep -Fq 'PASS browser: Not required' "$output/doctor.txt"
    if grep -Fq 'CS-WebUI' "$output/doctor.txt"; then
      cat "$output/doctor.txt" >&2
      exit 1
    fi
  fi

  # A plain build installs the frontend packages with the selected manager.
  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore
  test -d "$output/Frontend/node_modules"
  test -f "$output/Frontend/dist/index.html"
  # The frontend's packages and build output are not project items.
  dotnet msbuild "$output/$project_name.csproj" -getItem:None -getItem:Content -p:Configuration=Release > "$output/items.json"
  if grep -E '"Identity": "Frontend[\\/]+(node_modules|dist)[\\/]' "$output/items.json"; then
    echo "Frontend/node_modules or Frontend/dist is listed as a project item." >&2
    exit 1
  fi
  grep -Fq '"Identity": "Frontend/package.json"' "$output/items.json" || grep -Fq '"Identity": "Frontend\\package.json"' "$output/items.json"
  if [[ "$html_inputs" == true ]]; then
    test -f "$output/bin/Release/net10.0/www/settings.html"
  fi
  # The referenced host's client is the page's runic-views.js.
  cmp "$output/bin/Release/net10.0/www/$host_client" "$output/bin/Release/net10.0/www/runic-views.js"
  # A changed frontend rebuilds, and www/ mirrors the new dist/: the assets of
  # the earlier build are gone, and the host scripts stay (#236).
  local entry
  entry="$(find "$output/Frontend/src" -maxdepth 1 -name 'main.ts*' | head -n 1)"
  # A SvelteKit frontend has no main module; its universal hooks are part of the client bundle.
  [[ "$framework" == sveltekit ]] && entry="$output/Frontend/src/hooks.ts"
  printf '\nconsole.info("Runic template rebuild check");\n' >> "$entry"
  dotnet build "$output/$project_name.csproj" --configuration Release --no-restore > "$output/rebuild.log"
  local built_files served_files
  built_files="$(cd "$output/Frontend/dist" && find . -type f | sort)"
  served_files="$(cd "$output/bin/Release/net10.0/www" && find . -type f \
    ! -name runic-views.js ! -name runic-cswebui.js ! -name runic-desktop-views.js | sort)"
  if [[ "$built_files" != "$served_files" ]]; then
    echo "www/ does not mirror Frontend/dist after a frontend change." >&2
    diff <(echo "$built_files") <(echo "$served_files") >&2 || true
    exit 1
  fi
  grep -rFq 'Runic template rebuild check' "$output/bin/Release/net10.0/www"
  if [[ "$html_inputs" == true && "$manager" == npm ]]; then
    dotnet publish "$output/$project_name.csproj" --configuration Release --no-restore --no-build \
      --output "$output/publish" > "$output/publish.log"
    test -f "$output/publish/www/settings.html"
    cmp "$output/publish/www/$host_client" "$output/publish/www/runic-views.js"
  fi
  # Runic Desktop serves each Window below its own path, where root-absolute
  # asset URLs leave the page blank. Desktop variants are not started here.
  if grep -nE '(src|href)="/[^/]' "$output/Frontend/dist/index.html"; then
    echo "The built frontend document references root-absolute URLs." >&2
    exit 1
  fi
  test -f "$output/Frontend/src/generated/workspace.ts"
  test -f "$output/Frontend/src/generated/settings.ts"
  test -f "$output/Frontend/src/generated/preferences.ts"
  # One frontend build runs on every host: the built page loads runic-views.js
  # and no host-specific script, such as the Runic Desktop bootstrap.
  grep -Eq '<script\s[^>]*src="(\./)?runic-views\.js"' "$output/Frontend/dist/index.html"
  if grep -nE 'runic-(cswebui|desktop-views|desktop)\.js' "$output/Frontend/dist/index.html"; then
    echo "The built $host frontend loads a host-specific script." >&2
    exit 1
  fi
  # Every path in the README's project layout exists once the project is built.
  local layout_paths
  layout_paths="$(awk '/^## /{layout = ($0 == "## Project layout"); next} layout && /^\| `/' "$output/README.md" \
    | cut -d'|' -f2 | grep -o '`[^`]*`' | tr -d '`')"
  [[ -n "$layout_paths" ]]
  while IFS= read -r layout_path; do
    if [[ ! -e "$output/$layout_path" ]]; then
      echo "The generated README lists a missing project path: $layout_path" >&2
      exit 1
    fi
  done <<< "$layout_paths"
  if [[ "$framework" == vue && "$manager" == bun ]]; then
    # vue-tsc finds no .vue files under the Bun runtime and passes, so check
    # with Node as a plain `bun run` does.
    (cd "$output/Frontend" && bun run typecheck)
  else
    frontend_script "$manager" "$output/Frontend" typecheck
  fi
  if [[ "$host" == cswebui ]]; then
    serve_and_fetch "$output" "$output/serve-release.log" "$output/release-document.html" false \
      dotnet run --project "$output/$project_name.csproj" --configuration Release --no-build
  fi
  # The smoke lane opens the default Runic Desktop (GTK 3) project once, with
  # npm, and every GTK 4 variant.
  if [[ "${RUNIC_TEMPLATE_DESKTOP_SMOKE:-0}" == 1 ]] &&
    [[ "$linux_gtk" == 4 || ( "$host" == desktop && "$manager" == npm ) ]]; then
    run_desktop_smoke "$output" "$project_name" "$host_label" "$linux_gtk"
  fi
  if [[ "$tests" == tests ]]; then
    run_generated_tests "$output" "$project_name"
  fi
  printf 'TEMPLATE_OK|%s|%s|%s|%s|%s%s\n' "$framework" "$manager" "$host_label" "$view_models" "${tests:-no-tests}" "${restore:+|restore}"
}

# Runs the generated test project as its README documents. The tests build the
# application in Debug without its frontend, so no Debug frontend stamp appears.
run_generated_tests() {
  local output="$1"
  local project_name="$2"
  local log="$output/dotnet-test.log"
  if ! (cd "$output" && dotnet test --project "$project_name.Tests") > "$log" 2>&1; then
    echo "The generated tests failed." >&2
    tail -n 80 "$log" >&2
    exit 1
  fi
  if ! grep -Eq 'succeeded: 3\b' "$log" || grep -Eq 'failed: [1-9]' "$log"; then
    echo "The generated test run did not pass its three tests." >&2
    tail -n 40 "$log" >&2
    exit 1
  fi
  if [[ -e "$output/obj/Debug/net10.0/runic-bridge-frontend.stamp" ]]; then
    echo "Building the generated tests built the frontend." >&2
    exit 1
  fi
  # The test project uses the app's Bridges: TUnit sets IsTestProject and it declares
  # no Window or View, so it neither generates nor reports RUNICBRIDGE006 or 015.
  if [[ -e "$output/$project_name.Tests/obj/runic-bridge" || -e "$output/$project_name.Tests/obj/Debug/net10.0/runic-bridge" ]] \
    || grep -Eq 'RUNICBRIDGE[0-9]+' "$log"; then
    echo "Building the generated tests ran Bridge generation in the test project." >&2
    exit 1
  fi
  echo "TESTS_OK|$project_name"
}

# Runs the built Desktop application once under Xvfb with RUNIC_APPLICATION_CLOSE_AFTER_OPEN:
# the generated Program must open its embedded window, connect its bridge, and exit 0 without
# manual wiring. Needs Xvfb, dbus-run-session and WebKitGTK 4.1 (GTK 3) or GTK 4.12+ with
# WebKitGTK 6 (--linux-gtk 4).
run_desktop_smoke() {
  local output="$1"
  local project_name="$2"
  local host="$3"
  local linux_gtk="$4"
  local log="$output/desktop-smoke.log"
  local status=0
  # The application runs directly, not through dotnet run, so no build server keeps the session open.
  env -C "$output" RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1 GDK_BACKEND=x11 GSETTINGS_BACKEND=memory \
    timeout -k 10 180 dbus-run-session -- xvfb-run -a \
    dotnet "$output/bin/Release/net10.0/$project_name.dll" > "$log" 2>&1 < /dev/null || status=$?
  if [[ "$status" != 0 ]] || ! grep -Fq 'RUNIC_APPLICATION_OPENED=Embedded' "$log"; then
    echo "The $host application did not open its embedded window and exit (status $status)." >&2
    tail -n 80 "$log" >&2
    exit 1
  fi
  if [[ "$linux_gtk" == 4 ]]; then
    check_diagnostics_log "$output" "$project_name"
  fi
  echo "DESKTOP_SMOKE_OK|$project_name"
}

# The generated DiagnosticsLog keeps the Desktop diagnostics that standard error loses outside a terminal. A copy of
# the built application whose page never loads the Runic client reports presentation-connection-timeout; the log in
# the user's data directory, named after the application ID, holds it once, and a second start replaces the log.
check_diagnostics_log() {
  local output="$1"
  local project_name="$2"
  local copy="$template_tmp/diagnostics-$project_name"
  local data="$copy-data"
  local application_id log run
  application_id="$(sed -n 's|.*<ApplicationId>\(.*\)</ApplicationId>.*|\1|p' "$output/$project_name.csproj")"
  log="$data/$application_id/diagnostics.log"
  rm -rf "$copy" "$data"
  cp -R "$output/bin/Release/net10.0" "$copy"
  printf '<!doctype html><title>No Runic client</title>\n' > "$copy/www/index.html"
  for run in 1 2; do
    env -C "$copy" XDG_DATA_HOME="$data" GDK_BACKEND=x11 GSETTINGS_BACKEND=memory \
      timeout -k 10 120 dbus-run-session -- xvfb-run -a \
      dotnet "$copy/$project_name.dll" > "$copy/stderr-$run.log" 2>&1 < /dev/null || true
    if ! grep -Fq 'presentation-connection-timeout' "$copy/stderr-$run.log"; then
      echo "The $project_name copy without a Runic client reported no presentation-connection-timeout (run $run)." >&2
      tail -n 40 "$copy/stderr-$run.log" >&2
      exit 1
    fi
  done
  if [[ "$(grep -c 'presentation-connection-timeout' "$log" 2> /dev/null)" != 1 ]]; then
    echo "$log does not hold exactly the last start's diagnostic." >&2
    cat "$log" >&2 || true
    exit 1
  fi
  rm -rf "$copy" "$data"
  echo "DIAGNOSTICS_LOG_OK|$project_name"
}

# The guided creator must produce exactly what its printed dotnet new command
# produces, using the packaged tool and the candidate template.
verify_creator() {
  local creator_directory="$template_tmp/creator"
  # --application-id is a text option that the creator generates from the template model.
  local options=(--frontend vue --package-manager pnpm --host cswebui --view-models toolkit --tests --application-id org.example.AcceptanceCreator)
  dotnet tool install Runic.Create \
    --tool-path "$tool_directory" \
    --version "$package_version" \
    "${tool_source_options[@]}" \
    "${tool_restore_options[@]}"
  mkdir -p "$creator_directory/guided" "$creator_directory/direct"
  (cd "$creator_directory/guided" && "$tool_directory/runic-create" AcceptanceCreator "${options[@]}" \
    --template-source "$package_directory" --dry-run) > "$creator_directory/plan.txt"
  grep -Fq "dotnet new runic-app --name AcceptanceCreator ${options[*]}" "$creator_directory/plan.txt"
  grep -Fq "dnx Runic.Create@$package_version -- AcceptanceCreator ${options[*]}" "$creator_directory/plan.txt"
  test ! -e "$creator_directory/guided/AcceptanceCreator"
  (cd "$creator_directory/guided" && "$tool_directory/runic-create" AcceptanceCreator "${options[@]}" \
    --template-source "$package_directory") > "$creator_directory/create.txt"
  (cd "$creator_directory/direct" && dotnet new runic-app --name AcceptanceCreator "${options[@]}")
  diff -r "$creator_directory/guided/AcceptanceCreator" "$creator_directory/direct/AcceptanceCreator"
  grep -Fq 'dotnet runic dev' "$creator_directory/create.txt"
  if (cd "$creator_directory/guided" && "$tool_directory/runic-create" Other --frontend qt --yes --dry-run) \
    > "$creator_directory/invalid.txt" 2>&1; then
    echo "The creator accepted an unknown frontend." >&2
    exit 1
  fi
  grep -Fq 'react, vue, svelte, sveltekit, angular' "$creator_directory/invalid.txt"
  grep -Fq '<ApplicationId>org.example.AcceptanceCreator</ApplicationId>' "$creator_directory/guided/AcceptanceCreator/AcceptanceCreator.csproj"
  # --no-install uses the template installed above and prints only the dotnet new command.
  (cd "$creator_directory/guided" && "$tool_directory/runic-create" Offline --no-install --yes --dry-run) \
    > "$creator_directory/no-install.txt"
  if grep -Fq 'dotnet new install' "$creator_directory/no-install.txt"; then
    echo "The creator installs the template with --no-install." >&2
    exit 1
  fi
  # Accepting every default selects Runic Desktop and ReactiveUI.
  (cd "$creator_directory/guided" && "$tool_directory/runic-create" Defaults --yes --dry-run) \
    > "$creator_directory/defaults.txt"
  grep -Fq 'dotnet new runic-app --name Defaults --frontend react --package-manager npm --host desktop --linux-gtk 3 --view-models reactiveui' \
    "$creator_directory/defaults.txt"
  echo 'CREATOR_OK'
}

# Project names that are not npm names still produce a valid frontend package name.
verify_package_names() {
  local names_directory="$template_tmp/package-names"
  local case_entry project_name expected generated index=0
  local long_name
  long_name="$(printf 'a%.0s' {1..220})"
  mkdir -p "$names_directory"
  # Names without npm characters fall back to "app"; npm names have at most 214 characters.
  for case_entry in "Contoso.Notes App|contoso-notes-app" "_My__API.|my-api" "Café Notes|caf-notes" \
    "日本語|app" "___|app" "$long_name|${long_name:0:214}"; do
    project_name="${case_entry%%|*}"
    expected="${case_entry#*|}"
    index=$((index + 1))
    (cd "$names_directory" && dotnet new runic-app --name "$project_name" --output "case-$index" --frontend svelte)
    generated="$(bun -e 'process.stdout.write(require(process.argv[1]).name)' "$names_directory/case-$index/Frontend/package.json")"
    if [[ "$generated" != "$expected" ]]; then
      echo "Project '$project_name' produced frontend package name '$generated', expected '$expected'." >&2
      exit 1
    fi
  done
  # The application ID: com.example. and the name's ASCII letters and digits, starting with a letter, at most 50
  # characters (an MSIX package name), or the --application-id given.
  index=0
  for case_entry in "Contoso.Notes App|com.example.ContosoNotesApp" "_My__API.|com.example.MyAPI" \
    "Café Notes|com.example.CafNotes" "日本語|com.example.App" "2048 Game|com.example.App2048Game" \
    "$long_name|com.example.${long_name:0:38}"; do
    project_name="${case_entry%%|*}"
    expected="${case_entry#*|}"
    index=$((index + 1))
    (cd "$names_directory" && dotnet new runic-app --name "$project_name" --output "id-$index" --frontend svelte) > /dev/null
    check_application_id "$names_directory/id-$index" "$project_name" "$expected"
  done
  (cd "$names_directory" && dotnet new runic-app --name Notes --output id-explicit --application-id org.example.Notes --nix) > /dev/null
  check_application_id "$names_directory/id-explicit" Notes org.example.Notes
  grep -Fq 'name = "org.example.Notes";' "$names_directory/id-explicit/flake.nix"
  grep -Fq 'iconName = "org.example.Notes";' "$names_directory/id-explicit/flake.nix"
  echo 'PACKAGE_NAMES_OK'
  echo 'APPLICATION_IDS_OK'
  # --packaging adds packaging/ unchanged (the packaging CI job runs these files) and the properties it reads.
  local packaging_source="$repository_root/tools/Runic.Application.Templates/content/runic-app/packaging"
  if [[ -e "$names_directory/id-explicit/packaging" ]]; then
    echo "A project without --packaging has packaging/." >&2
    exit 1
  fi
  (cd "$names_directory" && dotnet new runic-app --name Packaged --output packaged --host desktop --linux-gtk 4 --packaging) > /dev/null
  if ! diff -r "$packaging_source" "$names_directory/packaged/packaging"; then
    echo "--packaging did not copy packaging/ unchanged." >&2
    exit 1
  fi
  for property in Description PackageProjectUrl RunicApplicationPublisher RunicApplicationUrlSchemes RunicApplicationFileTypes RunicApplicationWebLinkHosts RunicApplicationDBusActivatable; do
    grep -Fq "<$property>" "$names_directory/packaged/Packaged.csproj"
  done
  grep -Fxq '/artifacts/' "$names_directory/packaged/.gitignore"
  if node "$names_directory/packaged/packaging/package.mjs" android > /dev/null 2>&1; then
    echo "package.mjs accepted an unknown target." >&2
    exit 1
  fi
  echo 'PACKAGING_RECIPES_OK'
}

# The project file declares the application ID once, and the icons are copied unchanged.
check_application_id() {
  local output="$1"
  local project_name="$2"
  local expected="$3"
  local generated
  generated="$(sed -n 's|.*<ApplicationId>\(.*\)</ApplicationId>.*|\1|p' "$output/$project_name.csproj")"
  if [[ "$generated" != "$expected" ]]; then
    echo "Project '$project_name' declared application ID '$generated', expected '$expected'." >&2
    exit 1
  fi
  for icon in icon.svg icon.png icon.ico; do
    if ! cmp -s "$repository_root/tools/Runic.Application.Templates/content/runic-app/icons/$icon" "$output/icons/$icon"; then
      echo "Project '$project_name' did not get icons/$icon unchanged." >&2
      exit 1
    fi
  done
}

frameworks=(react vue svelte sveltekit angular)
managers=(npm pnpm bun)
# Every frontend and package manager uses the default host and ViewModels,
# Runic Desktop and ReactiveUI, created without --host and --view-models.
# Pairwise variants then cover each frontend with CS-WebUI and npm, whose
# served documents the checks fetch, each frontend with CommunityToolkit.Mvvm,
# and every host and ViewModel pairing. Variants and the npm projects add the
# test project and run it, so the tests pass with every host and ViewModel
# library. SvelteKit adds a variant with the opt-in restore post-action.
variants=(
  "react npm cswebui toolkit tests"
  "vue npm cswebui reactiveui tests"
  "svelte npm cswebui toolkit tests"
  "angular npm cswebui reactiveui tests"
  "svelte bun desktop toolkit tests"
  "angular pnpm desktop toolkit tests"
  "vue npm desktop toolkit tests gtk4"
  "sveltekit npm cswebui toolkit tests restore"
  "sveltekit bun desktop toolkit tests"
)
# Keep CI's default matrix complete while allowing focused local debugging.
if [[ -n "${RUNIC_TEMPLATE_FRAMEWORKS:-}" ]]; then
  read -r -a frameworks <<< "$RUNIC_TEMPLATE_FRAMEWORKS"
fi
if [[ -n "${RUNIC_TEMPLATE_MANAGERS:-}" ]]; then
  read -r -a managers <<< "$RUNIC_TEMPLATE_MANAGERS"
fi
if [[ -n "${RUNIC_TEMPLATE_VARIANTS+set}" ]]; then
  # Semicolon-separated "frontend manager host view-models [tests]" entries; empty skips them.
  IFS=';' read -r -a variants <<< "$RUNIC_TEMPLATE_VARIANTS"
else
  # Default variants follow the selected frontends, so per-framework CI lanes
  # together run each variant exactly once.
  selected_variants=()
  for variant in "${variants[@]}"; do
    if [[ " ${frameworks[*]} " == *" ${variant%% *} "* ]]; then selected_variants+=("$variant"); fi
  done
  variants=("${selected_variants[@]}")
fi
for framework in "${frameworks[@]}"; do
  case "$framework" in
    react|vue|svelte|sveltekit|angular) ;;
    *) echo "Unknown template framework: $framework" >&2; exit 2 ;;
  esac
done
for manager in "${managers[@]}"; do
  case "$manager" in
    npm|pnpm|bun) ;;
    *) echo "Unknown template package manager: $manager" >&2; exit 2 ;;
  esac
done

# CI runs the creator and package name checks in one framework lane only.
case "${RUNIC_TEMPLATE_CREATOR:-1}" in
  1) verify_creator; verify_package_names ;;
  0) ;;
  *) echo "RUNIC_TEMPLATE_CREATOR must be 0 or 1." >&2; exit 2 ;;
esac
for manager in "${managers[@]}"; do
  for framework in "${frameworks[@]}"; do
    if [[ "$manager" == npm ]]; then
      verify_template "$framework" "$manager" desktop reactiveui defaults tests
    else
      verify_template "$framework" "$manager" desktop reactiveui defaults
    fi
  done
done
for variant in "${variants[@]}"; do
  read -r -a fields <<< "$variant"
  verify_template "${fields[@]}"
done
