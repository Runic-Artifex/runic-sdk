import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import {
  mkdir,
  readdir,
  readFile,
  rename,
  rm,
  writeFile,
} from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const packageDirectory = import.meta.dirname;
const angularOutput = resolve(packageDirectory, ".angular-output");
const outputDirectory = resolve(packageDirectory, "dist");
const production = process.argv.includes("--production") ||
  process.env.NODE_ENV === "production";
const configuration = production ? "production" : "development";

await runAngularBuild(configuration);
await rm(outputDirectory, { recursive: true, force: true });
await rename(angularOutput, outputDirectory);

const builtDocumentPath = resolve(outputDirectory, "index.html");
const builtDocument = await readFile(builtDocumentPath, "utf8");
await rm(builtDocumentPath);

for (const demo of ["simple", "advanced"]) {
  const demoDirectory = resolve(outputDirectory, demo);
  await mkdir(demoDirectory, { recursive: true });
  await writeFile(
    resolve(demoDirectory, "index.html"),
    builtDocument
      .replaceAll("__TODO_DEMO__", demo)
      .replaceAll("__TODO_TITLE__", demo === "advanced" ? "Advanced ToDo" : "Simple ToDo"),
    "utf8",
  );
}

const outputFiles = await collectFiles(outputDirectory);
const app = outputFiles.find((path) => /^main(?:-[-_A-Za-z0-9]+)?\.js$/.test(path));
const styles = outputFiles.find((path) => /^styles(?:-[-_A-Za-z0-9]+)?\.css$/.test(path));
if (app === undefined || styles === undefined) {
  throw new Error("The Angular application builder did not emit its application assets.");
}

const manifest = {
  schema: "webuitoolkit.frontend-assets/1",
  framework: "Angular",
  mode: production ? "production" : "development",
  builder: "@angular/build:application",
  compilation: "aot",
  entrypoints: { app, styles },
  files: Object.fromEntries(await Promise.all(outputFiles.map(async (relativePath) => {
    const bytes = await readFile(resolve(outputDirectory, relativePath));
    return [relativePath, {
      bytes: bytes.byteLength,
      sha256: createHash("sha256").update(bytes).digest("hex"),
    }];
  }))),
};
await writeFile(
  resolve(outputDirectory, "webuitoolkit.assets.json"),
  JSON.stringify(manifest, null, 2) + "\n",
  "utf8",
);

async function runAngularBuild(configuration) {
  const angularCli = fileURLToPath(import.meta.resolve("@angular/cli/bin/ng.js"));
  await new Promise((resolveBuild, rejectBuild) => {
    const child = spawn(
      process.execPath,
      [angularCli, "build", "todo-angular", "--configuration", configuration],
      {
        cwd: packageDirectory,
        stdio: "inherit",
      },
    );
    child.once("error", rejectBuild);
    child.once("exit", (code, signal) => {
      if (code === 0) resolveBuild();
      else rejectBuild(new Error(
        signal === null
          ? `Angular application build exited with code ${code}.`
          : `Angular application build was terminated by ${signal}.`,
      ));
    });
  });
}

async function collectFiles(directory, relative = "") {
  const entries = await readdir(resolve(directory, relative), { withFileTypes: true });
  const files = [];
  for (const entry of entries.sort((left, right) =>
    left.name < right.name ? -1 : left.name > right.name ? 1 : 0)) {
    const path = relative.length === 0 ? entry.name : `${relative}/${entry.name}`;
    if (entry.isDirectory()) files.push(...await collectFiles(directory, path));
    else files.push(path);
  }
  return files;
}
