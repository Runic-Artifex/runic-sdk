#!/usr/bin/env node
import { execFile as execute } from "node:child_process";
import { createHash } from "node:crypto";
import { access, mkdtemp, readFile, readdir, realpath, rm, stat } from "node:fs/promises";
import { arch, platform, tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { promisify } from "node:util";

const repositoryRoot = resolve(import.meta.dirname, "..");
const schema = "runic.desktop.linux-native-quality/1";
const repeatSchema = "runic.desktop.linux-native-quality-repeat/1";
const execFile = promisify(execute);
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);

const evidence = {
  multiWindow: {
    surfaces: 2,
    concurrentWindowCycles: 24,
    postcondition: "application-not-running",
    tests: [
      "ContentAndLifecycleTests.SharedHostIsolatesSurfacesAndClosingOnePreservesTheOther",
      "ContentAndLifecycleTests.ConcurrentServerWindowChurnLeavesNoApplicationLifetimeBehind",
    ],
  },
  streaming: {
    headStreamFactoryCalls: 0,
    streamedFirstChunkBytes: 5,
    streamFactoryCalls: 1,
    cancellation: "request-disconnect-and-window-close",
    disposal: "required",
    tests: [
      "ContentAndLifecycleTests.StreamingVirtualContentHonorsHeadCancellationAndDisposal",
      "ContentAndLifecycleTests.StreamingVirtualContentCancelsAndDisposesWhenWindowCloses",
    ],
  },
  reconnect: {
    authenticatedConnections: 2,
    connectionIds: "distinct",
    test: "ManagedWindowTests.AcceptsAReconnectedBridgeAndRaisesNewConnectionEvents",
  },
  cleanRecovery: {
    ownedBrowserLaunches: 2,
    generatedProfiles: "unique-and-cleaned",
    unexpectedBrowserExit: "detected-and-cleaned",
    embeddedWebViewRestart: "second-surface-window",
    tests: [
      "BrowserHostTests.OwnsChromiumProcessProfileRestartAndNaturalExit",
      "Runic.Desktop.WebViewSmoke",
    ],
  },
  browserBridgeCapacity: {
    javascriptToDotnetPayloadBytes: 150000,
    rawResponseBytes: 3,
    test: "BrowserBridgeTests.UpstreamStyleCallbackAndJavaScriptRoundTripRunsInChromium",
    boundary: "structural-capacity-not-a-latency-budget",
  },
};

const exclusions = {
  accessibility: {
    status: "not-certified",
    reason: "Runic Desktop hosts consumer content; this runtime receipt has no representative application accessibility tree or assistive-technology journey.",
  },
  performance: {
    status: "not-certified",
    reason: "The retained 150000-byte bridge check is a structural capacity check, not a calibrated latency or throughput budget.",
  },
  memory: {
    status: "not-certified",
    reason: "No profiler-backed managed or native memory budget is measured by this receipt.",
  },
  windows: {
    status: "not-certified",
    reason: "This Linux-only receipt cannot certify WebView2 lifecycle, recovery, or diagnostics.",
  },
  macos: {
    status: "not-certified",
    reason: "This Linux-only receipt cannot certify WKWebView lifecycle, recovery, or diagnostics.",
  },
};

const managedTests = [
  "ContentAndLifecycleTests.SharedHostIsolatesSurfacesAndClosingOnePreservesTheOther",
  "ContentAndLifecycleTests.ConcurrentServerWindowChurnLeavesNoApplicationLifetimeBehind",
  "ContentAndLifecycleTests.StreamingVirtualContentHonorsHeadCancellationAndDisposal",
  "ContentAndLifecycleTests.StreamingVirtualContentCancelsAndDisposesWhenWindowCloses",
  "ManagedWindowTests.AcceptsAReconnectedBridgeAndRaisesNewConnectionEvents",
  "BrowserHostTests.OwnsChromiumProcessProfileRestartAndNaturalExit",
  "BrowserBridgeTests.UpstreamStyleCallbackAndJavaScriptRoundTripRunsInChromium",
];

const phases = [
  "locked-node-install",
  "contract-verification",
  "browser-transport-build",
  "managed-build",
  "representative-managed-quality-tests",
  "embedded-webview-smoke",
  "npm-package-consumer",
];

function fail(message) {
  throw new Error(`Linux native-quality receipt: ${message}`);
}

async function run(command, args, cwd = repositoryRoot) {
  try {
    return await execFile(command, args, { cwd, maxBuffer: 8 * 1024 * 1024 });
  } catch (error) {
    const output = [error.stdout, error.stderr].filter(Boolean).join("\n").slice(-8192);
    fail(`${command} ${args.join(" ")} failed.${output.length > 0 ? `\n${output}` : ""}`);
  }
}

async function readPackageFacts() {
  const [buildProps, packageProps, transport, contract] = await Promise.all([
    readFile(join(repositoryRoot, "Directory.Build.props"), "utf8"),
    readFile(join(repositoryRoot, "Directory.Packages.props"), "utf8"),
    readFile(join(repositoryRoot, "web/packages/desktop/package.json"), "utf8"),
    readFile(join(repositoryRoot, "contract/README.md"), "utf8"),
  ]);
  const prefix = match(buildProps, /<VersionPrefix[^>]*>([^<]+)<\/VersionPrefix>/, "Runic.Desktop version prefix");
  const suffix = match(buildProps, /<VersionSuffix[^>]*>([^<]+)<\/VersionSuffix>/, "Runic.Desktop version suffix");
  const webView2 = match(packageProps, /PackageVersion Include="Microsoft\.Web\.WebView2" Version="([^"]+)"/, "WebView2 package version");
  const contractVersion = match(contract, /Contract identity: `runic\.desktop\.presentation\/(\d+)`/, "contract version");
  const transportPackage = JSON.parse(transport);
  const version = `${prefix}-${suffix}`;
  if (transportPackage.name !== "@runic-artifex/desktop" || transportPackage.version !== version) {
    fail("the .NET and TypeScript Desktop package versions must be identical.");
  }
  return {
    dotnet: { identity: "Runic.Desktop", version },
    transport: { identity: transportPackage.name, version: transportPackage.version },
    webView2CompileDependency: { identity: "Microsoft.Web.WebView2", version: webView2 },
    contract: { id: "runic.desktop.presentation", version: Number(contractVersion) },
  };
}

async function readProfile() {
  if (platform() !== "linux" || arch() !== "x64") {
    fail("only the Linux x64 profile is supported; every other OS or architecture is excluded by this receipt.");
  }
  const browserPath = process.env.WEBUI_BROWSER_PATH;
  if (browserPath === undefined || browserPath.length === 0) {
    fail("WEBUI_BROWSER_PATH must name the Chromium binary selected for this receipt.");
  }
  await access(browserPath);
  if (!(await stat(browserPath)).isFile()) fail("WEBUI_BROWSER_PATH must name a browser executable.");
  const browser = await realpath(browserPath);
  if (!browser.includes("/nix/store/")) {
    fail("the Linux receipt requires a Nix-pinned Chromium path so its browser package can be identified exactly.");
  }
  const browserVersion = (await run(browser, ["--product-version"])).stdout.trim();
  if (!/^\d+(?:\.\d+){2,3}$/.test(browserVersion)) fail("Chromium did not report an exact product version.");

  const webKitDriver = await executableOnPath("WebKitWebDriver");
  const resolvedDriver = await realpath(webKitDriver);
  const webKitMatch = /\/nix\/store\/[^/]+-(webkitgtk-[^/]+)\/bin\/WebKitWebDriver$/.exec(resolvedDriver);
  if (webKitMatch === null) fail("WebKitWebDriver must come from a Nix-pinned WebKitGTK package.");
  const webKitPackage = webKitMatch[1];
  if (!(process.env.LD_LIBRARY_PATH ?? "").includes(`-${webKitPackage}/lib`)) {
    fail("LD_LIBRARY_PATH must bind the embedded WebView to the exact WebKitGTK package used by WebKitWebDriver.");
  }
  await executableOnPath("xvfb-run");

  const [sdk, runtimes, osRelease] = await Promise.all([
    run("dotnet", ["--version"]),
    run("dotnet", ["--list-runtimes"]),
    readFile("/etc/os-release", "utf8"),
  ]);
  const sdkVersion = sdk.stdout.trim();
  const runtimeMatch = /^Microsoft\.NETCore\.App\s+(10\.\d+\.\d+)\s+\[/m.exec(runtimes.stdout);
  if (!/^10\.\d+\.\d+$/.test(sdkVersion) || runtimeMatch === null) {
    fail("the Linux receipt requires a .NET 10 SDK and Microsoft.NETCore.App runtime.");
  }
  const osName = parseOsRelease(osRelease, "PRETTY_NAME");
  if (osName === undefined || osName.length === 0) fail("/etc/os-release must provide PRETTY_NAME.");

  return {
    os: { family: "linux", name: osName },
    architecture: "x64",
    runtime: { sdk: sdkVersion, framework: `Microsoft.NETCore.App ${runtimeMatch[1]}` },
    browser: { kind: "Chromium", version: browserVersion, executable: browser },
    embeddedWebView: { kind: "WebKitGTK", package: webKitPackage, driver: resolvedDriver },
    packages: await readPackageFacts(),
  };
}

async function executableOnPath(name) {
  const result = await run("which", [name]);
  const path = result.stdout.trim();
  if (path.length === 0) fail(`${name} is not available on PATH.`);
  await access(path);
  return path;
}

function parseOsRelease(value, key) {
  const line = value.split("\n").find((entry) => entry.startsWith(`${key}=`));
  return line?.slice(key.length + 1).replace(/^"|"$/g, "");
}

function match(value, expression, name) {
  const result = expression.exec(value);
  if (result?.[1] === undefined) fail(`could not read ${name}.`);
  return result[1];
}

async function readInputs() {
  return {
    implementationSha256: await hashPaths([
      "Directory.Build.props",
      "Directory.Packages.props",
      "src/Runic.Desktop",
      "web/packages/desktop/package.json",
      "web/packages/desktop/src",
    ]),
    evidenceSha256: await hashPaths([
      "tests/Runic.Desktop.Tests/BrowserBridgeTests.cs",
      "tests/Runic.Desktop.Tests/BrowserHostTests.cs",
      "tests/Runic.Desktop.Tests/ContentAndLifecycleTests.cs",
      "tests/Runic.Desktop.Tests/ManagedWindowTests.cs",
      "tests/Runic.Desktop.WebViewSmoke/Program.cs",
    ]),
  };
}

async function hashPaths(paths) {
  const files = [];
  for (const path of paths) await collectFiles(resolve(repositoryRoot, path), files);
  const hash = createHash("sha256");
  for (const path of files.sort()) {
    hash.update(relative(repositoryRoot, path));
    hash.update("\0");
    hash.update(await readFile(path));
    hash.update("\0");
  }
  return hash.digest("hex");
}

async function collectFiles(path, files) {
  const entry = await stat(path);
  if (entry.isFile()) {
    files.push(path);
    return;
  }
  for (const child of await readdir(path, { withFileTypes: true })) {
    if (child.isDirectory() && child.name !== "bin" && child.name !== "obj") {
      await collectFiles(join(path, child.name), files);
    }
    else if (child.isFile()) files.push(join(path, child.name));
  }
}

async function oneJourney() {
  const [profile, inputs] = await Promise.all([readProfile(), readInputs()]);
  const directory = await mkdtemp(join(tmpdir(), "runic-desktop-linux-quality-"));
  try {
    const trx = join(directory, "quality.trx");
    const filter = managedTests.map((name) => `FullyQualifiedName~${name}`).join("|");
    const completed = [];
    await run("npm", ["ci"]);
    completed.push("locked-node-install");
    await run("npm", ["run", "verify:contract"]);
    completed.push("contract-verification");
    await run("npm", ["run", "build"]);
    completed.push("browser-transport-build");
    await run("dotnet", ["restore", "RunicDesktop.slnx"]);
    await run("dotnet", ["build", "RunicDesktop.slnx", "--configuration", "Release", "--no-restore"]);
    completed.push("managed-build");
    await run("dotnet", [
      "test", "tests/Runic.Desktop.Tests/Runic.Desktop.Tests.csproj",
      "--configuration", "Release", "--no-build",
      "--filter", filter,
      "--logger", "trx;LogFileName=quality.trx",
      "--results-directory", directory,
    ]);
    await verifyTrx(trx);
    completed.push("representative-managed-quality-tests");
    await run("xvfb-run", [
      "-a", "dotnet", "run",
      "--project", "tests/Runic.Desktop.WebViewSmoke/Runic.Desktop.WebViewSmoke.csproj",
      "--configuration", "Release", "--no-build",
    ]);
    completed.push("embedded-webview-smoke");
    await run("./eng/verify-web-package.sh", []);
    completed.push("npm-package-consumer");

    if (!same(completed, phases)) fail("quality phases did not complete in the declared order.");
    return {
      schema,
      profile,
      inputs,
      evidence,
      exclusions,
      phases: completed.map((name) => ({ name, status: "passed", exitCode: 0 })),
    };
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

async function verifyTrx(path) {
  const xml = await readFile(path, "utf8");
  const results = [...xml.matchAll(/<UnitTestResult\b[^>]*\/>/g)].map(([element]) => ({
    name: attribute(element, "testName"),
    outcome: attribute(element, "outcome"),
  }));
  if (results.length !== managedTests.length) {
    fail(`the representative suite must report exactly ${managedTests.length} tests, but reported ${results.length}.`);
  }
  for (const name of managedTests) {
    const result = results.find((candidate) => candidate.name.endsWith(`.${name}`));
    if (result?.outcome !== "Passed") fail(`${name} did not report a passing TRX result.`);
  }
}

function attribute(element, name) {
  const result = new RegExp(`\\b${name}="([^"]*)"`).exec(element);
  return result?.[1] ?? "";
}

export function verifyReceipt(receipt, profile, inputs) {
  const errors = [];
  if (receipt?.schema !== repeatSchema || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) {
    errors.push("two Linux quality journeys are required");
  }
  for (const journey of receipt?.journeys ?? []) {
    if (journey?.schema !== schema || !same(journey?.evidence, evidence) || !same(journey?.exclusions, exclusions)) {
      errors.push("quality evidence or claim boundary differs");
    }
    if (!Array.isArray(journey?.phases) || !same(journey.phases.map((phase) => phase.name), phases) ||
        journey.phases.some((phase) => phase.status !== "passed" || phase.exitCode !== 0)) {
      errors.push("quality phases differ");
    }
    if (profile !== undefined && !same(journey?.profile, profile)) errors.push("receipt profile differs from the exact local profile");
    if (inputs !== undefined && !same(journey?.inputs, inputs)) errors.push("receipt implementation or evidence sources differ from the exact local inputs");
    if (inputs === undefined && (!/^[a-f0-9]{64}$/.test(journey?.inputs?.implementationSha256 ?? "") ||
        !/^[a-f0-9]{64}$/.test(journey?.inputs?.evidenceSha256 ?? ""))) {
      errors.push("receipt input fingerprints are malformed");
    }
  }
  if (receipt?.journeys?.length === 2 && !same(receipt.journeys[0], receipt.journeys[1])) {
    errors.push("Linux quality journeys are not deterministic");
  }
  return { ok: errors.length === 0, errors };
}

export async function runTwice() {
  const receipt = { schema: repeatSchema, journeys: [await oneJourney(), await oneJourney()] };
  const report = verifyReceipt(receipt, receipt.journeys[0]?.profile, receipt.journeys[0]?.inputs);
  if (!report.ok) fail(report.errors.join("; "));
  return receipt;
}

async function main(argv) {
  const [command, receiptPath] = argv;
  if (command === "run-twice" && receiptPath === undefined) {
    process.stdout.write(`${JSON.stringify(await runTwice(), null, 2)}\n`);
    return;
  }
  if (command === "verify-receipt" && receiptPath !== undefined && argv.length === 2) {
    const [profile, inputs] = await Promise.all([readProfile(), readInputs()]);
    const receipt = JSON.parse(await readFile(resolve(repositoryRoot, receiptPath), "utf8"));
    const report = verifyReceipt(receipt, profile, inputs);
    if (!report.ok) fail(report.errors.join("; "));
    return;
  }
  fail("usage: verify-linux-native-quality-receipt.mjs run-twice | verify-linux-native-quality-receipt.mjs verify-receipt <receipt>");
}

if (import.meta.main) {
  main(process.argv.slice(2)).catch((error) => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
