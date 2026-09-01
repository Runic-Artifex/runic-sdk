#!/usr/bin/env node

import { access, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { execFileSync, spawn } from "node:child_process";

const [authorityPath, bridgeArchive, angularArchive, receiptPath] = process.argv.slice(2);
if (authorityPath === undefined || bridgeArchive === undefined || angularArchive === undefined || receiptPath === undefined || process.argv.length !== 6) {
  throw new Error("Usage: node test-package-consumer.mjs <release-authority.json> <application-bridge.tgz> <angular.tgz> <receipt.json>");
}
await Promise.all([access(authorityPath), access(bridgeArchive), access(angularArchive)]);
const authority = JSON.parse(await readFile(authorityPath, "utf8"));
verifyAuthority(authority);
const candidates = await Promise.all([
  candidate(bridgeArchive, "@runic-artifex/application-bridge"),
  candidate(angularArchive, "@runic-artifex/angular"),
]);
if (candidates[0].version !== candidates[1].version) {
  throw new Error("Local Angular and Application Bridge candidates must select the same release train.");
}
if (candidates[1].dependencies?.["@runic-artifex/application-bridge"] === undefined) {
  throw new Error("The local Angular candidate must declare its Application Bridge dependency.");
}
console.log("Preparing clean Angular package consumer canary.");

const root = await mkdtemp(join(tmpdir(), "runic-angular-package-consumer."));
try {
  await write(root, "package.json", {
    name: "customer-shaped-angular-bridge-canary",
    private: true,
    scripts: { build: "ng build customer-app" },
    dependencies: {
      "@angular/common": "22.0.8",
      "@angular/core": "22.0.8",
      "@angular/platform-browser": "22.0.8",
      "@runic-artifex/application-bridge": `file:${resolve(bridgeArchive)}`,
      "@runic-artifex/angular": `file:${resolve(angularArchive)}`,
      "effect": "3.22.1",
      rxjs: "7.8.2",
    },
    overrides: {
      "@runic-artifex/application-bridge": `file:${resolve(bridgeArchive)}`,
    },
    devDependencies: {
      "@angular/build": "22.0.8",
      "@angular/cli": "22.0.8",
      "@angular/compiler": "22.0.8",
      "@angular/compiler-cli": "22.0.8",
      "ng-packagr": "22.0.2",
      typescript: "6.0.3",
    },
  });
  await write(root, "angular.json", angularJson());
  await write(root, "tsconfig.json", {
    compilerOptions: {
      target: "ES2022", module: "preserve", moduleResolution: "bundler", strict: true,
      skipLibCheck: true, experimentalDecorators: true,
    },
  });
  await write(root, "projects/contracts/tsconfig.lib.json", {
    extends: "../../tsconfig.json", compilerOptions: { outDir: "../../out-tsc/contracts" },
    include: ["src/**/*.ts"],
  });
  await write(root, "projects/contracts/package.json", {
    name: "@customer/contracts", version: "1.0.0", sideEffects: false,
    peerDependencies: { "@angular/core": "22.0.8" },
  });
  await write(root, "projects/contracts/ng-package.json", {
    $schema: "../../node_modules/ng-packagr/ng-package.schema.json",
    dest: "../../dist/contracts",
    lib: { entryFile: "src/public-api.ts" },
    allowedNonPeerDependencies: ["@runic-artifex/application-bridge", "effect"],
  });
  await write(root, "projects/contracts/src/public-api.ts", "export * from './lib/counter-contract.js';\nexport * from './lib/generated-translations.js';\n");
  await write(root, "projects/contracts/src/lib/generated-translations.ts", "// Generated catalog output; application code imports it as a normal ESM dependency.\nexport const m = Object.freeze({ counterTitle: () => 'Customer counter' });\n");
  await write(root, "projects/contracts/src/lib/counter-contract.ts", contractSource());
  await write(root, "projects/customer-app/tsconfig.app.json", {
    extends: "../../tsconfig.json", compilerOptions: { outDir: "../../out-tsc/customer-app" },
    files: ["src/main.ts"],
  });
  await write(root, "projects/customer-app/src/index.html", "<customer-root></customer-root>\n");
  await write(root, "projects/customer-app/src/main.ts", appSource());

  const environment = {
    ...process.env,
    NG_CLI_ANALYTICS: "false",
    npm_config_cache: join(root, ".npm-cache"),
    npm_config_update_notifier: "false",
  };
  await run("npm", ["install", "--ignore-scripts"], root, environment);
  await run("npm", ["exec", "ng", "build", "contracts"], root, environment);
  await run("npm", ["install", "--ignore-scripts", "./dist/contracts"], root, environment);
  await run("npm", ["run", "build"], root, environment);
  await writeReceipt(receiptPath, authorityPath, candidates);
  console.log("Angular package consumer canary passed.");
} finally {
  await rm(root, { recursive: true, force: true, maxRetries: 3 });
}

function verifyAuthority(authority) {
  const expectedCanonical = {
    identity: "@runic-artifex/angular", ecosystem: "npm", installKind: "npm-package", product: "application", state: "approved",
  };
  const canonical = authority.canonicalPackages?.filter((item) => item.identity === expectedCanonical.identity) ?? [];
  if (JSON.stringify(canonical) !== JSON.stringify([expectedCanonical])) {
    throw new Error("Release authority must declare one canonical @runic-artifex/angular package identity.");
  }
  const current = authority.currentPackages?.filter((item) => item.identity === expectedCanonical.identity) ?? [];
  if (current.length !== 1 || current[0].ecosystem !== "npm" || current[0].product !== "application" || current[0].stableOwner !== "Runic Application" || current[0].support !== "supported" || current[0].disposition !== "keep" || current[0].target !== expectedCanonical.identity || current[0].migration?.kind !== "package" || current[0].migration?.target !== expectedCanonical.identity) {
    throw new Error("Release authority must assign @runic-artifex/angular to Runic Application.");
  }
}

async function candidate(archive, identity) {
  const absolute = resolve(archive);
  const metadata = execFileSync("tar", ["-xOf", absolute, "package/package.json"], { encoding: "utf8" });
  const manifest = JSON.parse(metadata);
  if (manifest.name !== identity || !/^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$/u.test(manifest.version) || manifest.license !== "MIT" || manifest.repository?.url !== "git+https://github.com/Runic-Artifex/runic-toolkit.git" || manifest.exports === undefined) {
    throw new Error(`Invalid local candidate metadata for ${identity}.`);
  }
  const archiveStat = await stat(absolute);
  return {
    identity,
    version: manifest.version,
    archive: { name: absolute.split("/").at(-1), sha256: sha256(await readFile(absolute)), size: archiveStat.size },
    dependencies: manifest.dependencies,
  };
}

async function writeReceipt(destination, source, candidates) {
  const root = resolve(source, "..");
  const authority = await readFile(source);
  const revision = execFileSync("git", ["-C", root, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
  const tree = execFileSync("git", ["-C", root, "rev-parse", "HEAD^{tree}"], { encoding: "utf8" }).trim();
  if (!/^[0-9a-f]{40}$/u.test(revision) || !/^[0-9a-f]{40}$/u.test(tree)) {
    throw new Error("Could not resolve release authority Git provenance.");
  }
  await writeFile(destination, `${JSON.stringify({ schemaVersion: 1, consumer: "runic-toolkit.angular-package-canary/v1", releaseAuthority: { path: "runic.release.json", sha256: sha256(authority), revision, tree }, candidates }, null, 2)}\n`);
}

function sha256(value) { return createHash("sha256").update(value).digest("hex"); }

function angularJson() {
  return {
    $schema: "./node_modules/@angular/cli/lib/config/schema.json", version: 1,
    projects: {
      "customer-app": {
        projectType: "application", root: "projects/customer-app", sourceRoot: "projects/customer-app/src",
        architect: { build: { builder: "@angular/build:application", options: {
          outputPath: { base: "dist/customer-app", browser: "" }, browser: "projects/customer-app/src/main.ts",
          index: "projects/customer-app/src/index.html", tsConfig: "projects/customer-app/tsconfig.app.json",
        } } },
      },
      contracts: {
        projectType: "library", root: "projects/contracts", sourceRoot: "projects/contracts/src",
        architect: { build: { builder: "@angular/build:ng-packagr", options: {
          project: "projects/contracts/ng-package.json",
        } } },
      },
    },
  };
}

function contractSource() {
  return `import { Schema } from "effect";
import { defineApplicationContract } from "@runic-artifex/application-bridge";
export const CounterSnapshot = Schema.Struct({ count: Schema.Int, revision: Schema.Int.pipe(Schema.nonNegative()) });
export const CounterCommand = Schema.TaggedStruct("InitializeApplication", {});
export const CounterReceipt = Schema.TaggedStruct("ApplicationInitialized", { snapshot: CounterSnapshot });
export const CounterEvent = Schema.TaggedStruct("CounterChanged", { snapshot: CounterSnapshot });
export const CounterContract = defineApplicationContract({ identity: "customer.counter", version: 1, fingerprint: "4e873f5967e86eeded5e26d8faf27c305464f1272b90935cc8a1b09365471508", command: CounterCommand, receipt: CounterReceipt, event: CounterEvent, snapshot: CounterSnapshot, initialize: { _tag: "InitializeApplication" } as const });
export type CounterCommand = typeof CounterCommand.Type;
export type CounterReceipt = typeof CounterReceipt.Type;
export type CounterEvent = typeof CounterEvent.Type;
export type CounterSnapshot = typeof CounterSnapshot.Type;
`;
}

function appSource() {
  return `import { Component, provideZonelessChangeDetection } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { MockApplicationBridge, createApplicationBridgeController } from "@runic-artifex/application-bridge";
import { injectApplicationBridge, provideApplicationBridge } from "@runic-artifex/angular";
import { Effect } from "effect";
import { CounterCommand, CounterContract, m, type CounterEvent, type CounterReceipt, type CounterSnapshot } from "@customer/contracts";
const bridge = createApplicationBridgeController(CounterContract, MockApplicationBridge({ initialize: () => Effect.succeed({ count: 0, revision: 0 }), dispatch: () => Effect.succeed({ _tag: "ApplicationInitialized", snapshot: { count: 0, revision: 0 } }) }));
@Component({ selector: "customer-root", standalone: true, template: "{{ title }} {{ client.snapshot()?.count }}" })
class CustomerApp { readonly title = m.counterTitle(); readonly client = injectApplicationBridge<CounterCommand, CounterReceipt, CounterEvent, CounterSnapshot>(); constructor() { void this.client.initialize(); } }
void bootstrapApplication(CustomerApp, { providers: [provideZonelessChangeDetection(), provideApplicationBridge({ controller: bridge, snapshotFromEvent: event => event._tag === "CounterChanged" ? event.snapshot : undefined })] });
`;
}

async function write(root, path, value) {
  const file = join(root, path);
  await (await import("node:fs/promises")).mkdir(join(file, ".."), { recursive: true });
  await writeFile(file, typeof value === "string" ? value : `${JSON.stringify(value, null, 2)}\n`);
}

function run(command, arguments_, cwd, env) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, arguments_, { cwd, env, stdio: "inherit" });
    child.once("error", reject);
    child.once("exit", code => code === 0 ? resolve() : reject(new Error(`${command} exited with ${code}.`)));
  });
}
