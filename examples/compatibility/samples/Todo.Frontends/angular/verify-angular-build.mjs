import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

const packageDirectory = import.meta.dirname;
const manifest = JSON.parse(await readFile(
  resolve(packageDirectory, "dist/webuitoolkit.assets.json"),
  "utf8",
));
if (manifest.builder !== "@angular/build:application" ||
    manifest.compilation !== "aot" ||
    manifest.mode !== "production") {
  throw new Error("The Angular Todo sample was not produced by the production AOT application builder.");
}

const configuration = JSON.parse(await readFile(
  resolve(packageDirectory, "angular.json"),
  "utf8",
));
const build = configuration.projects["todo-angular"].architect.build;
if (build.builder !== "@angular/build:application" ||
    build.options.aot !== true ||
    build.configurations.production.aot !== true) {
  throw new Error("Angular application-builder/AOT configuration is missing.");
}

const source = await readFile(resolve(packageDirectory, "src/main.ts"), "utf8");
if (source.includes('import "@angular/compiler"')) {
  throw new Error("The Angular Todo sample still opts into browser-side JIT compilation.");
}

const application = await readFile(
  resolve(packageDirectory, "dist", manifest.entrypoints.app),
  "utf8",
);
for (const marker of ["JitCompiler", "@angular/compiler", "JIT compilation failed"]) {
  if (application.includes(marker)) {
    throw new Error(`The AOT application bundle unexpectedly contains ${marker}.`);
  }
}

for (const demo of ["simple", "advanced"]) {
  const document = await readFile(
    resolve(packageDirectory, `dist/${demo}/index.html`),
    "utf8",
  );
  if (!document.includes(`<base href="../">`) ||
      !document.includes(`data-demo="${demo}"`) ||
      !document.includes(`src="${manifest.entrypoints.app}"`)) {
    throw new Error(`The ${demo} Angular document does not resolve the CsWebUi application bundle.`);
  }
}

console.log("Angular production application-builder/AOT output verified.");
