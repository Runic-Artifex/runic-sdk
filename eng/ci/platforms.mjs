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
  // Cold Chromium and Node timers stall on Windows runners (#70).
  { os: "windows-latest", rid: "win-x64", provider: "Windows", extension: ".exe", cswebui: true, "smoke-timeout-scale": "2", platform: "win-x64" },
  { os: "windows-11-arm", rid: "win-arm64", provider: "Windows", extension: ".exe", cswebui: false, "smoke-timeout-scale": "2", platform: "win-arm64" },
  { os: "macos-26", rid: "osx-arm64", provider: "MacOS", extension: "", cswebui: true, platform: "osx-arm64" },
];
export const wpfLegs = [
  { os: "windows-2025", rid: "win-x64", platform: "win-x64" },
  { os: "windows-11-arm", rid: "win-arm64", platform: "win-arm64" },
];
export const packagingLegs = [
  { os: "windows-latest", target: "windows", platform: "win-x64" },
  { os: "macos-26", target: "macos", platform: "osx-arm64" },
  { os: "ubuntu-24.04", target: "flatpak" },
];

// The job names, as GitHub reports them, of every Windows and macOS leg. Pull requests run only
// the legs their changes need, so a release requires one successful main run with all of them.
export const releaseJobs = [
  ...nativeLegs.filter(leg => leg.platform).map(leg => `Native / ${leg.rid}`),
  ...wpfLegs.map(leg => `WPF navigation / ${leg.rid}`),
  ...packagingLegs.filter(leg => leg.platform).map(leg => `Packaging / ${leg.target}`),
];

// Path rules, first match wins. A path names a file or directory; a trailing `*` matches any
// path that starts with the rest. Owned files without a rule (web packages, templates, assets,
// the ReactiveUI adapters, examples and documentation) need no Windows or macOS leg; unowned files
// need every leg unless they are neutral.
const all = platforms;
export const platformRules = [
  // GTK 4 and the Linux providers run on the Linux legs only.
  { platforms: [], paths: ["packages/dotnet/Runic.Desktop.Gtk4", "tests/native/Runic.Desktop.Gtk4.Smoke",
    "packages/dotnet/Runic.Platform.Linux*", "tests/dotnet/Runic.Platform.Linux*", "eng/portals"] },
  // The native window layer, platform contracts and runtime, and the packaging recipes and probe.
  { platforms: all, paths: ["packages/dotnet/Runic.Desktop", "tests/dotnet/Runic.Desktop.Tests", "tests/fixtures/desktop",
    "tests/native/Runic.Desktop.ComparativeStressAdapter", "packages/dotnet/Runic.Platform", "packages/dotnet/Runic.Platform.Runtime",
    "tests/dotnet/Runic.Platform.Runtime.Tests", "tests/native/Runic.Desktop.WebViewSmoke", "tests/native/Runic.Desktop.AccessibilityProbe",
    "eng/build/desktop.props", "eng/branding/desktop", "tools/Runic.Application.Templates/content/runic-app/packaging",
    "tools/Runic.Application.Templates/content/runic-app/ApplicationIdentity.cs", "tools/Runic.Application.Templates/content/runic-app/icons",
    "tests/native/Runic.Packaging.Probe"] },
  // The managed Windows and View runtime, its NativeAOT apps, probes and file watching, and the test host whose tests
  // run there (Runic.Application.Views.Wpf.Windows.Tests references it too): x64 Windows and macOS.
  { platforms: ["win-x64", "osx-arm64"], paths: ["packages/dotnet/Runic.Application.Views", "packages/dotnet/Runic.Application.Views.Desktop",
    "packages/dotnet/Runic.Application.Views.CsWebUi", "examples/first-window", "tests/fixtures/application/cswebui-window-probes",
    "packages/dotnet/Runic.Application.Testing", "tests/dotnet/Runic.Application.Testing.Tests"] },
  // The Windows providers and elevation, on both Windows architectures.
  { platforms: ["win-x64", "win-arm64"], paths: ["packages/dotnet/Runic.Platform.Windows*", "tests/dotnet/Runic.Platform.Windows*",
    "tests/native/Runic.Elevation.Smoke", "tests/native/windows-notifications"] },
  // WPF hosts, the navigation engine they host, the ReactiveUI adapters (the WPF suite checks their failure bookkeeping
  // under WPF's dispatcher; Runic.Navigation also carries their model-turn flow), the Bridge generator, the IDE host
  // and Windows administration.
  { platforms: ["win-x64"], paths: ["packages/dotnet/Runic.Navigation", "packages/dotnet/Runic.Navigation.Wpf", "tests/dotnet/Runic.Navigation.Wpf.Tests",
    "packages/dotnet/Runic.Application.Views.ReactiveUI*",
    "tests/fixtures/navigation/wpf-consumer", "packages/dotnet/Runic.Application.Views.Wpf", "tests/dotnet/Runic.Application.Views.Wpf.Tests",
    "tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests", "tests/fixtures/application/wpf-bridge-consumer", "examples/wpf-*",
    "tests/native/wpf-ui-automation",
    "tools/Runic.Application.Views.Codegen*", "tools/Runic.Application.Views.Dev", "examples/notes-reactive-views",
    "packages/dotnet/Runic.Platform.Administration.Windows", "tests/dotnet/Runic.Platform.Administration.Windows.Tests",
    "examples/dotnet/Runic.Administration.Console", "tools/Runic.Administration.Verify"] },
  { platforms: ["osx-arm64"], paths: ["packages/dotnet/Runic.Platform.MacOS*", "tests/dotnet/Runic.Platform.MacOS*", "tests/native/platform-sandbox"] },
];

// Unowned files that no Windows or macOS job reads: Nix, release tooling, the solutions and the
// shipping-project generator. They still plan every Linux job.
export const neutralPaths = ["nix", "nixos", "flake.nix", "flake.lock", "eng/release", "eng/verify-packages.mjs",
  "RunicSdk.slnx", "RunicSdk.Core.slnx", "eng/generate-shipping-projects.mjs"];

export const matchesPath = (file, path) => path.endsWith("*") ? file.startsWith(path.slice(0, -1)) : file === path || file.startsWith(`${path}/`);
