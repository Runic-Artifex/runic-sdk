import { createHash } from "node:crypto";
import { mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { build } from "vite";

const packageDirectory = import.meta.dirname;
const outputDirectory = resolve(packageDirectory, "dist");
const buildLockDirectory = resolve(packageDirectory, ".build-lock");
const production = process.argv.includes("--production") ||
  process.env.NODE_ENV === "production";
const watch = process.argv.includes("--watch");

const emitStableEntrypoints = {
  name: "webuitoolkit-cwhtml-entrypoints",
  async closeBundle() {
    const manifestPath = resolve(outputDirectory, "vite.manifest.json");
    const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
    const entry = manifest["src/main.js"];
    if (entry?.isEntry !== true || typeof entry.file !== "string" ||
        !Array.isArray(entry.css) || entry.css.length !== 1) {
      throw new Error("The cwhtml Vite build did not emit one JavaScript and one CSS entrypoint.");
    }

    await writeFile(
      resolve(outputDirectory, "cwhtml.js"),
      `void import("./${entry.file}");\n`,
      "utf8",
    );
    await writeFile(
      resolve(outputDirectory, "cwhtml.css"),
      `@import url("./${entry.css[0]}");\n`,
      "utf8",
    );
    await writeAssetManifest(entry);
  },
};

if (!watch) {
  await acquireBuildLock();
}

try {
  await rm(outputDirectory, { recursive: true, force: true });

  const result = await build({
    root: packageDirectory,
    configFile: false,
    publicDir: false,
    logLevel: "info",
    plugins: [emitStableEntrypoints],
    build: {
      outDir: outputDirectory,
      emptyOutDir: true,
      target: "es2022",
      sourcemap: true,
      minify: production ? "oxc" : false,
      manifest: "vite.manifest.json",
      watch: watch ? {} : null,
      rollupOptions: {
        input: {
          app: resolve(packageDirectory, "src/main.js"),
        },
        output: {
          entryFileNames: production ? "assets/[name]-[hash].js" : "assets/[name].js",
          chunkFileNames: production ? "assets/[name]-[hash].js" : "assets/[name].js",
          assetFileNames: production
            ? "assets/[name]-[hash][extname]"
            : "assets/[name][extname]",
        },
      },
    },
  });

  if (watch) {
    const watcher = result;
    const stop = async () => {
      await watcher.close();
      process.exit(0);
    };
    process.once("SIGINT", stop);
    process.once("SIGTERM", stop);
  }
} finally {
  if (!watch) {
    await rm(buildLockDirectory, { recursive: true, force: true });
  }
}

async function acquireBuildLock() {
  const deadline = Date.now() + 120_000;
  while (true) {
    try {
      await mkdir(buildLockDirectory);
      await writeFile(
        resolve(buildLockDirectory, "owner"),
        `${process.pid}\n`,
        "utf8",
      );
      return;
    } catch (error) {
      if (error?.code !== "EEXIST") {
        throw error;
      }
      const owner = await readBuildLockOwner();
      if (owner !== null && !isProcessRunning(owner)) {
        await rm(buildLockDirectory, { recursive: true, force: true });
        continue;
      }
      if (Date.now() >= deadline) {
        throw new Error(
          `Timed out waiting for the shared cwhtml Vite build lock at ${buildLockDirectory}.`,
        );
      }
      await new Promise((resolveDelay) => setTimeout(resolveDelay, 50));
    }
  }
}

async function readBuildLockOwner() {
  try {
    const value = Number.parseInt(
      await readFile(resolve(buildLockDirectory, "owner"), "utf8"),
      10,
    );
    return Number.isSafeInteger(value) && value > 0 ? value : null;
  } catch (error) {
    if (error?.code === "ENOENT") {
      return null;
    }
    throw error;
  }
}

function isProcessRunning(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error?.code !== "ESRCH";
  }
}

async function writeAssetManifest(entry) {
  const files = await collectFiles(outputDirectory);
  const manifest = {
    schema: "webuitoolkit.frontend-assets/1",
    framework: "cwhtml-htmx",
    mode: production ? "production" : "development",
    entrypoints: {
      app: "cwhtml.js",
      styles: "cwhtml.css",
      compiledApp: entry.file,
      compiledStyles: entry.css[0],
    },
    files: Object.fromEntries(await Promise.all(files
      .filter((path) => path !== "webuitoolkit.assets.json")
      .map(async (relativePath) => {
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
}

async function collectFiles(directory, relative = "") {
  await mkdir(directory, { recursive: true });
  const entries = await readdir(resolve(directory, relative), { withFileTypes: true });
  const files = [];
  for (const entry of entries.sort((left, right) =>
    left.name < right.name ? -1 : left.name > right.name ? 1 : 0)) {
    const path = relative.length === 0 ? entry.name : `${relative}/${entry.name}`;
    if (entry.isDirectory()) {
      files.push(...await collectFiles(directory, path));
    } else {
      files.push(path);
    }
  }
  return files;
}
