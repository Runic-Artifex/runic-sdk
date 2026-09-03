#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const toolkit = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const suite = resolve(toolkit, "..");
const repositories = {
  desktop: "runic-desktop",
  toolkit: "runic-toolkit",
  assets: "runic-assets",
  vite: "runic-vite",
  svelte: "runic-svelte",
  examples: "runic-toolkit-examples",
  translationsEditor: "runic-translations-editor",
};

const sourceRevisions = Object.fromEntries(
  Object.entries(repositories).map(([name, directory]) => [name, revision(resolve(suite, directory))]),
);
const templateManifest = json(resolve(
  toolkit,
  "templates/RunicToolkit.Templates/content/svelte/Contract/bridge.ir.json",
));
const setupManifest = json(resolve(
  suite,
  "runic-toolkit-examples/samples/03-SetupApplication/Contract/bridge.ir.json",
));
const editorManifest = json(resolve(
  suite,
  "runic-translations-editor/Contract/bridge.ir.json",
));
const bridgePackage = json(resolve(toolkit, "web/packages/application-bridge/package.json"));
const desktopPackage = json(resolve(suite, "runic-desktop/web/packages/desktop/package.json"));
const vitePackage = json(resolve(suite, "runic-vite/package.json"));
const sveltePackage = json(resolve(suite, "runic-svelte/packages/svelte/package.json"));

const receipt = {
  schema: "runic.desktop.w100-golden-path/1",
  sourceRevisions,
  contracts: {
    templateCounter: contract(templateManifest),
    setupExample: contract(setupManifest),
    translationsEditor: contract(editorManifest),
  },
  packages: {
    applicationBridge: `${bridgePackage.name}@${bridgePackage.version}`,
    desktop: `${desktopPackage.name}@${desktopPackage.version}`,
    vite: `${vitePackage.name}@${vitePackage.version}`,
    svelte: `${sveltePackage.name}@${sveltePackage.version}`,
  },
  capabilityProfiles: {
    default: ["private-loopback", "request-scoped-assets", "browser", "embedded-webview"],
    compatibility: ["cs-webui"],
  },
  exclusions: [
    "patched-webui-abi",
    "public-network-listener",
    "duplicate-application-bridge",
    "duplicate-asset-or-localization-authority",
    "automatic-cs-webui-source-migration",
  ],
  validation: {
    isolatedTemplateJourneys: ["react", "svelte"],
    consumerApplications: ["sveltekit-setup", "translations-editor"],
    lifecycle: ["startup", "cancellation", "reconnect", "shutdown", "recovery"],
    delivery: ["embedded-assets", "streaming", "vite-hmr", "browser-webview"],
  },
};

const serialized = `${JSON.stringify(receipt, null, 2)}\n`;
const outputIndex = process.argv.indexOf("--output");
if (outputIndex >= 0) {
  const output = process.argv[outputIndex + 1];
  if (!output) throw new Error("--output requires a path.");
  writeFileSync(resolve(output), serialized, "utf8");
} else {
  process.stdout.write(serialized);
}

function revision(repository) {
  return execFileSync("git", ["-C", repository, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
}

function json(path) {
  return JSON.parse(readFileSync(path, "utf8"));
}

function contract(manifest) {
  const fingerprint = manifest.fingerprint.value;
  if (typeof fingerprint !== "string" || !/^[0-9a-f]{64}$/.test(fingerprint)) {
    throw new Error("A W100 contract manifest has no canonical fingerprint.");
  }
  return {
    identity: manifest.protocol.identity,
    version: manifest.protocol.version,
    fingerprint,
  };
}
