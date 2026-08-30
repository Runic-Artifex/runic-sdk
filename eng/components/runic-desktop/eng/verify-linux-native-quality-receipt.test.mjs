import assert from "node:assert/strict";
import test from "node:test";
import { verifyReceipt } from "./verify-linux-native-quality-receipt.mjs";

const profile = {
  os: { family: "linux", name: "NixOS" },
  architecture: "x64",
  runtime: { sdk: "10.0.302", framework: "Microsoft.NETCore.App 10.0.10" },
  browser: { kind: "Chromium", version: "150.0.7871.186", executable: "/nix/store/browser/bin/chromium" },
  embeddedWebView: { kind: "WebKitGTK", package: "webkitgtk-2.52.5+abi=4.1", driver: "/nix/store/webkit/bin/WebKitWebDriver" },
  packages: {
    dotnet: { identity: "Runic.Desktop", version: "1.0.0-preview.1" },
    transport: { identity: "@runic-artifex/desktop", version: "1.0.0-preview.1" },
    webView2CompileDependency: { identity: "Microsoft.Web.WebView2", version: "1.0.4078.44" },
    contract: { id: "runic.desktop.presentation", version: 1 },
  },
};
const inputs = { implementationSha256: "a".repeat(64), evidenceSha256: "b".repeat(64) };

const journey = () => ({
  schema: "runic.desktop.linux-native-quality/1",
  profile,
  inputs,
  evidence: {
    multiWindow: { surfaces: 2, concurrentWindowCycles: 24, postcondition: "application-not-running", tests: ["ContentAndLifecycleTests.SharedHostIsolatesSurfacesAndClosingOnePreservesTheOther", "ContentAndLifecycleTests.ConcurrentServerWindowChurnLeavesNoApplicationLifetimeBehind"] },
    streaming: { headStreamFactoryCalls: 0, streamedFirstChunkBytes: 5, streamFactoryCalls: 1, cancellation: "request-disconnect-and-window-close", disposal: "required", tests: ["ContentAndLifecycleTests.StreamingVirtualContentHonorsHeadCancellationAndDisposal", "ContentAndLifecycleTests.StreamingVirtualContentCancelsAndDisposesWhenWindowCloses"] },
    reconnect: { authenticatedConnections: 2, connectionIds: "distinct", test: "ManagedWindowTests.AcceptsAReconnectedBridgeAndRaisesNewConnectionEvents" },
    cleanRecovery: { ownedBrowserLaunches: 2, generatedProfiles: "unique-and-cleaned", unexpectedBrowserExit: "detected-and-cleaned", embeddedWebViewRestart: "second-surface-window", tests: ["BrowserHostTests.OwnsChromiumProcessProfileRestartAndNaturalExit", "Runic.Desktop.WebViewSmoke"] },
    browserBridgeCapacity: { javascriptToDotnetPayloadBytes: 150000, rawResponseBytes: 3, test: "BrowserBridgeTests.UpstreamStyleCallbackAndJavaScriptRoundTripRunsInChromium", boundary: "structural-capacity-not-a-latency-budget" },
  },
  exclusions: {
    accessibility: { status: "not-certified", reason: "Runic Desktop hosts consumer content; this runtime receipt has no representative application accessibility tree or assistive-technology journey." },
    performance: { status: "not-certified", reason: "The retained 150000-byte bridge check is a structural capacity check, not a calibrated latency or throughput budget." },
    memory: { status: "not-certified", reason: "No profiler-backed managed or native memory budget is measured by this receipt." },
    windows: { status: "not-certified", reason: "This Linux-only receipt cannot certify WebView2 lifecycle, recovery, or diagnostics." },
    macos: { status: "not-certified", reason: "This Linux-only receipt cannot certify WKWebView lifecycle, recovery, or diagnostics." },
  },
  phases: ["locked-node-install", "contract-verification", "browser-transport-build", "managed-build", "representative-managed-quality-tests", "embedded-webview-smoke", "npm-package-consumer"].map((name) => ({ name, status: "passed", exitCode: 0 })),
});

test("accepts an exact repeated Linux quality receipt", () => {
  const value = { schema: "runic.desktop.linux-native-quality-repeat/1", journeys: [journey(), journey()] };
  assert.deepEqual(verifyReceipt(value, profile, inputs), { ok: true, errors: [] });
});

test("rejects softened limits, missing exclusions, or a different local profile", () => {
  const value = { schema: "runic.desktop.linux-native-quality-repeat/1", journeys: [journey(), journey()] };
  value.journeys[1].evidence.multiWindow.concurrentWindowCycles = 1;
  delete value.journeys[1].exclusions.memory;
  assert.equal(verifyReceipt(value, profile, inputs).ok, false);
  assert.equal(verifyReceipt({ schema: value.schema, journeys: [journey(), journey()] }, { ...profile, architecture: "arm64" }, inputs).ok, false);
});
