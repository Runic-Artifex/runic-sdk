#!/usr/bin/env node

import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const matrix = json("eng/frontend-support-matrix.json");
if (matrix.schema !== "runic-toolkit.frontend-support-matrix/2") {
  fail("Unsupported frontend support matrix schema.");
}

const expected = ["react", "vue", "svelte", "angular"];
if (JSON.stringify(Object.keys(matrix.frontends)) !== JSON.stringify(expected)) {
  fail("The support matrix must list React, Vue, Svelte, and Angular in policy order.");
}

for (const gate of Object.values(matrix.sharedGates)) requirePath(gate);

const sdkProps = text(
  "src/RunicToolkit.Frontend.Sdk/buildTransitive/RunicToolkit.Frontend.Sdk.props",
);
contains(sdkProps, "RunicToolkitFrontendCompilerEnabled", "external compiler integration seam");
contains(sdkProps, "RunicToolkitFrontendDevServerKind", "generic development-server property");
contains(sdkProps, "RunicToolkitFrontendDevServerDocument", "native bootstrap document property");

const owners = {
  react: ["web/packages/mvvm-react/src/application.ts", "startReactMvvmApplication", "react"],
  vue: ["web/packages/mvvm-vue/src/index.ts", "startVueMvvmApplication", "vue"],
  svelte: ["web/packages/mvvm-svelte/src/application.ts", "startSvelteMvvmApplication", "svelte"],
  angular: ["web/packages/mvvm-angular/src/application.ts", "startAngularMvvmApplication", "@angular/core"],
};

for (const [framework, [ownerPath, symbol, peer]] of Object.entries(owners)) {
  const entry = matrix.frontends[framework];
  if (entry.owner !== symbol || entry.package !== `@runic-artifex/mvvm-${framework}`) {
    fail(`${framework} has inconsistent owner or package metadata.`);
  }
  contains(text(ownerPath), symbol, `${framework} native application owner`);

  const package_ = json(`web/packages/mvvm-${framework}/package.json`);
  if (package_.name !== entry.package) fail(`${framework} package identity is inconsistent.`);
  if (package_.license !== "MIT") fail(`${framework} package must use MIT.`);
  if (package_.dependencies?.["@runic-artifex/mvvm"] !== package_.version) {
    fail(`${framework} must consume the exact matching MVVM core version.`);
  }
  if (package_.peerDependencies?.[peer] === undefined) {
    fail(`${framework} must declare its UI framework as a peer dependency.`);
  }
}

const core = json("web/packages/mvvm/package.json");
if (core.name !== "@runic-artifex/mvvm" || core.license !== "MIT") {
  fail("The framework-neutral MVVM package has inconsistent identity metadata.");
}

const conformance = json("web/packages/conformance/package.json");
if (conformance.dependencies?.["@runic-artifex/mvvm"] !== core.version) {
  fail("The conformance package must consume the exact matching MVVM core version.");
}

const inspector = text("web/packages/mvvm/src/inspector.ts");
contains(inspector, "MvvmDevelopmentInspector", "private-binding inspector");
contains(inspector, "mountMvvmInspectorOverlay", "native inspector overlay");
contains(text("web/packages/mvvm/src/mock.ts"), "MvvmMockFrameChannel", "protocol mock");
contains(text("web/packages/mvvm/src/native.ts"), "channelFactory", "production-owner channel seam");

console.log("Frontend adapter policy passed for React, Vue, Svelte, and Angular.");

function json(path) {
  return JSON.parse(text(path));
}

function text(path) {
  const absolute = resolve(root, path);
  if (!existsSync(absolute)) fail(`Missing required frontend artifact '${path}'.`);
  return readFileSync(absolute, "utf8");
}

function requirePath(path) {
  if (!existsSync(resolve(root, path))) fail(`Missing required frontend path '${path}'.`);
}

function contains(source, expected, label) {
  if (!source.includes(expected)) fail(`Missing ${label}: '${expected}'.`);
}

function fail(message) {
  throw new Error(message);
}
