import { createHash } from "node:crypto";
import { mkdir, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { build } from "vite";

export async function buildTodoFrontend(options) {
  const packageDirectory = resolve(options.packageDirectory);
  const outputDirectory = resolve(packageDirectory, "dist");
  await rm(outputDirectory, { recursive: true, force: true });
  const production = process.argv.includes("--production") ||
    process.env.NODE_ENV === "production";

  await build({
    root: packageDirectory,
    configFile: false,
    publicDir: false,
    logLevel: "info",
    resolve: {
      alias: options.alias ?? {},
    },
    plugins: options.plugins ?? [],
    esbuild: {
      jsx: "automatic",
      tsconfigRaw: {
        compilerOptions: {
          experimentalDecorators: true,
          useDefineForClassFields: false,
        },
      },
    },
    build: {
      outDir: outputDirectory,
      emptyOutDir: true,
      target: "es2022",
      sourcemap: true,
      minify: production ? "oxc" : false,
      manifest: "vite.manifest.json",
      rollupOptions: {
        input: {
          app: resolve(packageDirectory, options.entry),
          styles: resolve(packageDirectory, "../shared/styles.css"),
        },
        output: {
          entryFileNames: production ? "assets/[name]-[hash].js" : "assets/[name].js",
          assetFileNames: production
            ? "assets/[name]-[hash][extname]"
            : "assets/[name][extname]",
        },
      },
    },
  });

  const assetNames = await readdir(resolve(outputDirectory, "assets"));
  const app = assetNames.find((name) => /^app(?:-[-_A-Za-z0-9]+)?\.js$/.test(name));
  const styles = assetNames.find((name) => /^styles(?:-[-_A-Za-z0-9]+)?\.css$/.test(name));
  if (app === undefined || styles === undefined) {
    throw new Error("The frontend build did not emit its application assets.");
  }

  for (const demo of ["simple", "advanced"]) {
    const demoDirectory = resolve(outputDirectory, demo);
    await mkdir(demoDirectory, { recursive: true });
    await writeFile(
      resolve(demoDirectory, "index.html"),
      document(options.framework, demo, app, styles),
      "utf8",
    );
  }

  const files = await collectFiles(outputDirectory);
  const manifest = {
    schema: "webuitoolkit.frontend-assets/1",
    framework: options.framework,
    mode: production ? "production" : "development",
    entrypoints: { app: `assets/${app}`, styles: `assets/${styles}` },
    files: Object.fromEntries(await Promise.all(files.map(async (relativePath) => {
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

function document(framework, demo, app, styles) {
  const title = demo === "advanced" ? "Advanced ToDo" : "Simple ToDo";
  const rootElement = framework === "Angular" ? "todo-app" : "main";
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${title} · ${framework}</title>
  <link rel="stylesheet" href="../vendor/bootstrap/bootstrap.min.css">
  <link rel="stylesheet" href="../vendor/fontawesome/css/fontawesome.min.css">
  <link rel="stylesheet" href="../vendor/fontawesome/css/regular.min.css">
  <link rel="stylesheet" href="../vendor/fontawesome/css/solid.min.css">
  <link rel="stylesheet" href="../assets/${styles}">
  <script src="../webui.js"></script>
</head>
<body data-demo="${demo}" data-framework="${framework}">
  <${rootElement} id="app" aria-live="polite">
    <div class="app-shell"><div class="alert alert-secondary">Connecting to the native C# ViewModel…</div></div>
  </${rootElement}>
  <script type="module" src="../assets/${app}"></script>
</body>
</html>
`;
}
