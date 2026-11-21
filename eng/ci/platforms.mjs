// Which Windows and macOS legs SDK CI runs, as plain data. eng/ci/plan.mjs plans the legs, and
// eng/release/ci-run.mjs, which runs with the runner's Node before any setup, checks that a
// release commit ran them all. No imports.

// The platforms planned per change. Linux legs run whenever their job runs.
export const platforms = ["win-x64", "win-arm64", "osx-arm64"];

// A pull request with this label runs every platform; the plan step reads the labels when it
// runs, so rerunning all jobs after adding it is enough.
export const allPlatformsLabel = "ci:all-platforms";

// The legs of the platform-dependent jobs. `platform` names the platform a leg needs; legs
// without one run whenever the job runs. The other keys are the job's matrix values.
export const nativeLegs = [
  // cswebui: whether CS-WebUI ships a native library for the RID, so the job runs its
  // window probes (eng/support.json).
  { os: "ubuntu-24.04", rid: "linux-x64", provider: "Linux", extension: "", cswebui: true },
  { os: "ubuntu-24.04-arm", rid: "linux-arm64", provider: "Linux", extension: "", cswebui: true },
  // win-x64, the longest leg, runs as two shards of about equal time (C2 of D077.25; the shard
  // conditions are in ci.yml's native job). Both are release jobs.
  { os: "windows-latest", rid: "win-x64", shard: "window", provider: "Windows", extension: ".exe", cswebui: true, platform: "win-x64" },
  { os: "windows-latest", rid: "win-x64", shard: "platform", provider: "Windows", extension: ".exe", cswebui: true, platform: "win-x64" },
  { os: "windows-11-arm", rid: "win-arm64", provider: "Windows", extension: ".exe", cswebui: false, platform: "win-arm64" },
  { os: "macos-26", rid: "osx-arm64", provider: "MacOS", extension: "", cswebui: true, platform: "osx-arm64" },
];
// RUNIC_TEST_TIMEOUT_SCALE per runner, as `${RUNNER_OS}-${RUNNER_ARCH}`: how much longer than on a developer machine the
// test and smoke condition waits may take (eng/ci/README.md, "Test timeouts"). .github/actions/test-timeout-scale sets
// it in every job that runs tests. Larger where waits stall most: cold Chromium and WebView2 starts on Windows (#70),
// the Windows Arm and macOS runners.
export const testTimeoutScales = { "Linux-X64": "4", "Linux-ARM64": "4", "Windows-X64": "5", "Windows-ARM64": "6", "macOS-ARM64": "6" };
export const wpfLegs = [
  { os: "windows-2025", rid: "win-x64", platform: "win-x64" },
  { os: "windows-11-arm", rid: "win-arm64", platform: "win-arm64" },
];
// The screen readers whose speech the accessibility probe checks (Runic.Desktop.AccessibilityProbe --screen-reader),
// one job each; VoiceOver runs in the native job's macOS leg. They are speech checks, not release jobs.
export const screenReaderLegs = [
  { reader: "orca", os: "ubuntu-24.04" },
  { reader: "nvda", os: "windows-2025", platform: "win-x64" },
];
// The paths that plan the screen reader legs: the embedded windows, the WPF host, the accessibility fixtures and the
// probe.
export const screenReaderPaths = ["packages/dotnet/Runic.Desktop", "packages/dotnet/Runic.Desktop.Gtk4",
  "packages/dotnet/Runic.Application.Views.Wpf", "tests/native/Runic.Desktop.WebViewSmoke", "tests/native/Runic.Desktop.Gtk4.Smoke",
  "tests/native/Runic.Desktop.AccessibilityProbe", "tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests/ScreenReaderChecks.cs",
  "eng/ci/windows-nvda.ps1"];
// The files the webkit-cdp job reads: dotnet runic dev's CDP adapter for WebKitGTK (#1029), the Linux hosts it attaches
// to, the template it checks and the check itself. It runs on Linux only.
export const webKitCdpPaths = ["tools/dotnet-runic", "packages/dotnet/Runic.Desktop", "packages/dotnet/Runic.Desktop.Gtk4",
  "packages/dotnet/Runic.Platform.Linux", "packages/dotnet/Runic.Platform.Linux.Gtk4", "tools/Runic.Application.Templates",
  "tests/webkit-cdp", "tests/templates/check-webkit-cdp.mjs", "tests/templates/Test-Templates.sh"];
export const packagingLegs = [
  { os: "windows-latest", target: "windows", platform: "win-x64" },
  { os: "macos-26", target: "macos", platform: "osx-arm64" },
  { os: "ubuntu-24.04", target: "flatpak" },
  { os: "ubuntu-24.04", target: "appimage" },
  { os: "ubuntu-24.04", target: "deb" },
  // Built on Ubuntu, installed in the Fedora container.
  { os: "ubuntu-24.04", target: "rpm" },
  // dotnet runic package flathub --build: the manifest built offline as Flathub does.
  { os: "ubuntu-24.04", target: "flathub" },
];

// The native job's name for a leg, as ci.yml's `name:` gives it.
export const nativeJob = leg => `Native / ${leg.rid}${leg.shard ? ` (${leg.shard})` : ""}`;
// The job names, as GitHub reports them, of every Windows and macOS leg. Pull requests run only
// the legs their changes need, so a release requires one successful main run with all of them.
export const releaseJobs = [
  ...nativeLegs.filter(leg => leg.platform).map(nativeJob),
  ...wpfLegs.map(leg => `WPF navigation / ${leg.rid}`),
  ...packagingLegs.filter(leg => leg.platform).map(leg => `Packaging / ${leg.target}`),
];

// Path rules, first match wins. A path names a file or directory; a trailing `*` matches any
// path that starts with the rest. Owned files without a rule (web packages, templates, the asset adapters,
// the ReactiveUI adapters, examples and documentation) need no Windows or macOS leg; unowned files
// need every leg unless they are neutral.
const all = platforms;
// dotnet runic's files that start programs or decide which program and how (#1081); see the win-x64 rule below. A tool
// file that creates a ProcessStartInfo or calls Process.Start must be CommandRunner.cs (plan.test.mjs).
export const windowsProgramStartPaths = [
  "tools/dotnet-runic/CommandRunner.cs", "tools/dotnet-runic/ExecutableSearch.cs", "tools/dotnet-runic/JavaScriptPackageManager.cs",
  "tools/dotnet-runic/RunningProcess.cs", "tools/dotnet-runic/HostProcessController.cs",
  "tools/dotnet-runic/ViteDevelopmentServer.cs", "tools/dotnet-runic/AngularDevelopmentServer.cs",
  "tests/dotnet/Runic.Application.Tool.Tests/ToolTests.WindowsCommands.cs",
  "tests/fixtures/application/dev-live-assets/dev-npm-smoke.mjs",
];
// dotnet runic's replace of files that other processes read (#1102): its retry of a rename that Windows refuses, and the
// check of it against a held file (ToolTests' WindowsCommands category, which the native job's Windows x64 leg runs).
// Its callers only call it, so they plan by their own rules.
export const windowsFileReplacePaths = [
  "tools/dotnet-runic/FileReplace.cs", "tests/dotnet/Runic.Application.Tool.Tests/DevSessionFileRetryTests.cs",
];
// dotnet runic dev --browser's code file (#1187): readable only by its user, which on Windows is an access list of that
// user alone with nothing inherited. Its check is in ToolTests' WindowsCommands category, which the native job's Windows
// x64 leg runs.
export const windowsPrivateFilePaths = [
  "tools/dotnet-runic/BrowserEntry.cs", "tests/dotnet/Runic.Application.Tool.Tests/DevBrowserCodeFileTests.cs",
];
// dev's loopback listeners on 127.0.0.1 and [::1] (DEV-SEC-80): Windows binds a port on the two addresses by rules of its
// own. Their checks are in ToolTests' WindowsCommands category, which the native job's Windows x64 leg runs.
export const windowsLoopbackPaths = [
  "tools/dotnet-runic/LoopbackListeners.cs", "tests/dotnet/Runic.Application.Tool.Tests/LoopbackFamilyTests.cs",
];
// Runic.Spelling's personal word list (DEV-SEC-88): readable only by its user, which on Windows is an access list of that
// user alone with nothing inherited, written as dev --browser's code file is (PrivateFiles.cs). Its check is in
// Runic.Spelling.Tests' WindowsPrivateFiles category, which the native job's Windows x64 leg runs.
export const windowsSpellingPrivateFilePaths = [
  "packages/dotnet/Runic.Spelling/PrivateFiles.cs", "packages/dotnet/Runic.Spelling/SpellingUserWords.cs",
  "tests/dotnet/Runic.Spelling.Tests/PrivateFileTests.cs",
];
export const platformRules = [
  // GTK 4, the Linux providers and the manual global menu check run on the Linux legs only.
  { platforms: [], paths: ["packages/dotnet/Runic.Desktop.Gtk4", "tests/native/Runic.Desktop.Gtk4.Smoke", "tests/native/Runic.Desktop.GlobalMenuCheck",
    "packages/dotnet/Runic.Platform.Linux*", "tests/dotnet/Runic.Platform.Linux*", "eng/portals",
    // The CI scripts of Linux-only steps: the real portal container, the musl checks in Alpine and the RPM install.
    "eng/ci/real-portal.sh", "eng/ci/native-musl.sh", "eng/ci/packaging-rpm.sh"] },
  // The native window layer, platform contracts and runtime, and the packaging recipes, dotnet runic package and the probe.
  { platforms: all, paths: ["packages/dotnet/Runic.Desktop", "packages/dotnet/Runic.Desktop.Kestrel", "tests/dotnet/Runic.Desktop.Tests", "tests/fixtures/desktop",
    "tests/native/Runic.Desktop.ComparativeStressAdapter", "packages/dotnet/Runic.Platform", "packages/dotnet/Runic.Platform.Runtime",
    "tests/dotnet/Runic.Platform.Runtime.Tests", "tests/native/Runic.Desktop.WebViewSmoke", "tests/native/Runic.Desktop.AccessibilityProbe",
    // runic-host/1 window mode in every built-in web view (Runic.Application.Views.Desktop's host).
    "tests/native/Runic.Application.HostedWindows.Smoke",
    "eng/build/desktop.props", "eng/branding/desktop", "tools/Runic.Application.Templates/content/runic-app/packaging",
    "tools/Runic.Application.Templates/content/runic-app/ApplicationIdentity.cs", "tools/Runic.Application.Templates/content/runic-app/icons",
    "tests/native/Runic.Packaging.Probe", "tools/dotnet-runic/Packaging",
    // The packaging job's receipt check and the executable's icon check (sdk#1207), which its Windows and macOS legs run too.
    "eng/ci/packaging-probe-check.mjs", "eng/ci/windows-icon-check.mjs"] },
  // The managed Windows and View runtime, its NativeAOT apps, probes and file watching, the development asset source,
  // and the test host whose tests
  // run there (Runic.Application.Views.Wpf.Windows.Tests references it too): x64 Windows and macOS.
  { platforms: ["win-x64", "osx-arm64"], paths: ["packages/dotnet/Runic.Application.Views", "packages/dotnet/Runic.Application.Views.Desktop",
    "packages/dotnet/Runic.Application.Views.CsWebUi", "examples/first-window", "tests/fixtures/application/cswebui-window-probes",
    "packages/dotnet/Runic.Application.Testing", "tests/dotnet/Runic.Application.Testing.Tests",
    // The development-directory source's per-OS root and watcher, tested in the native job's Windows and macOS step,
    // and its Desktop adapter, which the Runic Desktop Views host references.
    "packages/dotnet/Runic.Assets", "packages/dotnet/Runic.Assets.Desktop", "tests/dotnet/Runic.Assets.Tests",
    // The CS-WebUI blob endpoint's checks, which the native job's Windows and macOS steps run.
    "tests/dotnet/Runic.Application.Views.Desktop.Tests/CsWebUiBlobEndpointChecks*", "eng/ci/windows-httpsys-*",
    // The Bridge diagnostics catalog, which the Views build targets and the generator read.
    "docs/runicbridge-diagnostics.json"] },
  // The hosted components widget's Angular frontend and its browser smoke run in the views job on Linux only; the
  // Windows probe builds the default frontend.
  { platforms: [], paths: ["examples/hosted-components/Widget/Angular", "examples/hosted-components/angular-component-smoke.mjs"] },
  // The region mode examples, whose probes run in the native job's Windows x64 leg as the elevated pair (#865, #866).
  { platforms: ["win-x64"], paths: ["examples/hosted-region", "examples/hosted-components"] },
  // The template's launch profiles and its VS Code WebView2 launch configurations (D066.25 3), which the native
  // job's Windows x64 leg checks against WebView2 with the dev-live-assets fixture (webview2-debug-smoke.mjs), and the
  // session file and contract lines that check reads.
  { platforms: ["win-x64"], paths: ["tools/Runic.Application.Templates/content/runic-app/Properties/launchSettings.json",
    "tools/Runic.Application.Templates/content/runic-app/.vscode/launch.json", "tests/fixtures/application/dev-live-assets",
    "tools/dotnet-runic/DevSession.cs", "tools/dotnet-runic/DevApplication.cs"] },
  // How dotnet runic starts programs on Windows (#1081): npm and pnpm are batch files there (npm.cmd). The native job's
  // Windows x64 leg tests the resolution, quoting and stop (ToolTests.WindowsCommands.cs) and runs dev with npm
  // (dev-npm-smoke.mjs). Only the files that start programs or choose which and how plan that leg: every process start
  // of the tool goes through CommandRunner, which plan.test.mjs checks in the tool's sources.
  { platforms: ["win-x64"], paths: windowsProgramStartPaths },
  // How dotnet runic replaces a file that another process holds on Windows (#1102), above.
  { platforms: ["win-x64"], paths: windowsFileReplacePaths },
  // dev --browser's private code file on Windows (#1187), above.
  { platforms: ["win-x64"], paths: windowsPrivateFilePaths },
  // dev's listeners on both loopback addresses (DEV-SEC-80), above.
  { platforms: ["win-x64"], paths: windowsLoopbackPaths },
  // Runic.Spelling's private word list on Windows (DEV-SEC-88), above.
  { platforms: ["win-x64"], paths: windowsSpellingPrivateFilePaths },
  // The NVDA leg's driver, which runs in the screen reader job on Windows x64 only.
  { platforms: ["win-x64"], paths: ["eng/ci/windows-nvda.ps1"] },
  // The Windows providers and elevation, on both Windows architectures.
  { platforms: ["win-x64", "win-arm64"], paths: ["packages/dotnet/Runic.Platform.Windows*", "tests/dotnet/Runic.Platform.Windows*",
    "tests/native/Runic.Elevation.Smoke", "tests/native/windows-notifications",
    // The Windows legs' CI scripts: the first-sign-in dismissal (native and WPF jobs) and the test host runner with its
    // crash report (native job).
    "eng/ci/windows-dismiss-oobe.ps1", "eng/ci/windows-native-run.mjs", "eng/ci/windows-crash-report.ps1"] },
  // WPF hosts, the navigation engine they host, the ReactiveUI adapters (the WPF suite checks their failure bookkeeping
  // under WPF's dispatcher; Runic.Navigation also carries their model-turn flow), the Bridge generator, the IDE host
  // and Windows administration.
  // The Runic.Platform analyzers run in the compiler only; the WPF suite builds with Runic.Platform, which packs them.
  { platforms: ["win-x64"], paths: ["packages/dotnet/Runic.Platform.Analyzers", "packages/dotnet/Runic.Platform.Analyzers.CodeFixes"] },
  { platforms: ["win-x64"], paths: ["packages/dotnet/Runic.Navigation", "packages/dotnet/Runic.Navigation.Wpf", "tests/dotnet/Runic.Navigation.Wpf.Tests",
    "packages/dotnet/Runic.Application.Views.ReactiveUI*",
    "tests/fixtures/navigation/wpf-consumer", "packages/dotnet/Runic.Application.Views.Wpf", "tests/dotnet/Runic.Application.Views.Wpf.Tests",
    "tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests", "tests/fixtures/application/wpf-bridge-consumer", "examples/wpf-*",
    "tests/native/wpf-ui-automation",
    "tools/Runic.Application.Views.Codegen*", "tools/Runic.Application.Views.Dev", "examples/notes-reactive-views",
    // The Bridge shape analyzers run in the compile of every Views project, the WPF ones included.
    "packages/dotnet/Runic.Application.Views.Analyzers*",
    "packages/dotnet/Runic.Platform.Administration.Windows", "tests/dotnet/Runic.Platform.Administration.Windows.Tests",
    "examples/dotnet/Runic.Administration.Console", "tools/Runic.Administration.Verify"] },
  // The macOS providers and the App Sandbox fixtures (the sandboxed Runic Desktop app runs in the macOS packaging leg).
  { platforms: ["osx-arm64"], paths: ["packages/dotnet/Runic.Platform.MacOS*", "tests/dotnet/Runic.Platform.MacOS*", "tests/native/platform-sandbox",
    "tests/native/Runic.Sandbox.Probe",
    // The macOS legs' CI scripts: VoiceOver, appearance and open probes (native job), hdiutil retries (packaging job).
    "eng/ci/macos-*", "eng/ci/hdiutil.sh"] },
];

// Unowned files that no Windows or macOS job reads: Nix, release tooling, the solutions, the
// shipping-project generator and the Linux-only CI helpers. They still plan every Linux job.
export const neutralPaths = ["nix", "nixos", "flake.nix", "flake.lock", ".envrc", "eng/release", "eng/verify-packages.mjs",
  "RunicSdk.slnx", "RunicSdk.Core.slnx", "eng/generate-shipping-projects.mjs",
  "eng/ci/apt-install.sh", "eng/ci/permit-webkit-sandbox.sh", "eng/ci/test-managed.mjs",
  // The coordinator's release-candidate feed for the editors checklist (eng/release/README.md); CI never runs it.
  "eng/ci/candidate-feed.mjs", "eng/ci/candidate-feed"];

// Unowned on purpose: shared inputs of every job (the SDK and package versions, MSBuild defaults, lock files, the
// workflow and its actions, the shared CI and build scripts, the test timeouts). A change to one plans every
// job and every leg. Every tracked file is owned by a component, engineering-only (eng/run.mjs), neutral or listed
// here; plan.test.mjs fails on a file that is none of these, so a new file gets an owner instead of silently
// planning everything.
export const globalPaths = [".config/dotnet-tools.json", ".editorconfig", ".gitattributes", ".node-version", "global.json", "package.json",
  "bun.lock", "NuGet.config", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "LICENSE",
  ".github/workflows/ci.yml", ".github/actions", "eng/Versions.props", "eng/build", "eng/workspace.json", "eng/run.mjs", "eng/test.mjs",
  "eng/toolchain.mjs", "eng/version.mjs", "eng/support.json", "eng/support.mjs", "eng/nupkg.mjs", "eng/node-compatibility.mjs",
  "eng/source-map-comments.mjs", "eng/verify-templates.mjs", "eng/dependencies", "eng/ci/build-artifact.mjs", "eng/ci/source-state.mjs",
  "eng/ci/retry.sh", "eng/ci/retried-tests.mjs", "eng/ci/quarantine.mjs", "eng/ci/flakes.json", "eng/ci/test-timeout-scale.mjs", "eng/ci/train-feed.mjs", "tests/support"];

export const matchesPath = (file, path) => path.endsWith("*") ? file.startsWith(path.slice(0, -1)) : file === path || file.startsWith(`${path}/`);
