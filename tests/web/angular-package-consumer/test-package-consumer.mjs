#!/usr/bin/env node

import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";

const execute = promisify(execFileCallback);
const root = await mkdtemp(join(tmpdir(), "runic-angular-views-consumer-"));
try {
  let archive = process.argv[2];
  if (process.argv.length > 3) throw new Error("Usage: node test-package-consumer.mjs [views-angular.tgz]");
  if (!archive) {
    await execute("npm", ["run", "build", "--workspace", "@runic-artifex/views-angular"]);
    const packed = await execute("npm", ["pack", "--json", "--workspace", "@runic-artifex/views-angular", "--pack-destination", root]);
    const result = JSON.parse(packed.stdout);
    archive = join(root, (Array.isArray(result) ? result[0] : Object.values(result)[0]).filename);
  }
  archive = resolve(archive);
  const manifest = JSON.parse((await execute("tar", ["-xOf", archive, "package/package.json"])).stdout);
  assert.equal(manifest.name, "@runic-artifex/views-angular");
  assert.equal(manifest.private, undefined);
  assert.equal(manifest.license, "MIT");
  const archiveJs = (await execute("tar", ["-xOf", archive, "package/dist/esm/view-outlet.js"])).stdout;
  const archiveTypes = (await execute("tar", ["-xOf", archive, "package/dist/esm/view-outlet.d.ts"])).stdout;
  assert.match(archiveJs, /ɵɵngDeclareComponent/);
  assert.match(archiveJs, /isStandalone: true/);
  assert.match(archiveTypes, /static ɵcmp:/);
  await writeFile(join(root, "package.json"), JSON.stringify({ private: true, type: "module", scripts: { build: "ng build" }, dependencies: {
    "@runic-artifex/views-angular": `file:${archive}`,
    "@angular/common": "22.1.5", "@angular/core": "22.1.5", "@angular/platform-browser": "22.1.5", "rxjs": "7.8.2"
  }, devDependencies: {
    "@angular/build": "22.1.7", "@angular/cli": "22.1.7", "@angular/compiler": "22.1.5",
    "@angular/compiler-cli": "22.1.5", "typescript": "6.0.3"
  } }), "utf8");
  await execute("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", "--package-lock=false"], { cwd: root });
  await writeFile(join(root, "angular.json"), JSON.stringify({
    $schema: "./node_modules/@angular/cli/lib/config/schema.json", version: 1,
    projects: { consumer: { projectType: "application", root: "", sourceRoot: "src", architect: {
      build: { builder: "@angular/build:application", options: {
        outputPath: "dist", browser: "src/main.ts", index: "src/index.html", tsConfig: "tsconfig.json"
      } }
    } } }, defaultProject: "consumer"
  }), "utf8");
  await writeFile(join(root, "tsconfig.json"), JSON.stringify({ compilerOptions: {
    target: "ES2022", module: "preserve", moduleResolution: "bundler", strict: true, skipLibCheck: true,
    experimentalDecorators: true
  }, angularCompilerOptions: { strictTemplates: true }, include: ["src/**/*.ts"] }), "utf8");
  await mkdir(join(root, "src"));
  await writeFile(join(root, "src/index.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><base href=\"/\"></head><body><consumer-root></consumer-root></body></html>", "utf8");
  await writeFile(join(root, "src/main.ts"), `import { Component, input, provideZonelessChangeDetection, type InputSignal } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { RunicViewOutlet, type ViewRegistry } from "@runic-artifex/views-angular";
type Page = { readonly kind: "counter"; connect(): Promise<unknown> };
@Component({ selector: "counter-page", standalone: true, template: "{{ page().kind }}" })
class CounterPage { readonly page: InputSignal<Page> = input.required<Page>(); }
@Component({ selector: "consumer-root", standalone: true, imports: [RunicViewOutlet],
  template: '<runic-view-outlet [content]="current" [registry]="registry" />' })
class ConsumerRoot {
  readonly current: Page = { kind: "counter", connect: async () => undefined };
  readonly registry = { counter: CounterPage } satisfies ViewRegistry<Page>;
}
void bootstrapApplication(ConsumerRoot, { providers: [provideZonelessChangeDetection()] });
`, "utf8");
  await execute("npm", ["run", "build"], { cwd: root });
  assert.match(await readFile(join(root, "dist", "browser", "index.html"), "utf8"), /consumer-root/);
  console.log("Angular Views package consumer passed.");
} finally {
  await rm(root, { recursive: true, force: true });
}
