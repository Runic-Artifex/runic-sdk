import { copyFileSync, mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";

// Bundles the page into one module. Inside the SDK repository @runic-artifex/react
// is its workspace source, and React resolves from that package's install, so
// there is one copy. A copied-out frontend installs both packages instead.
const binding = fileURLToPath(new URL("../../../packages/web/react/", import.meta.url));
const result = await Bun.build({
  entrypoints: ["src/app.tsx"],
  outdir: "dist",
  target: "browser",
  format: "esm",
  naming: "app.js",
  define: { "process.env.NODE_ENV": JSON.stringify("production") },
  plugins: [{
    name: "workspace-react",
    setup(build) {
      build.onResolve({ filter: /^@runic-artifex\/react$/ }, () => ({ path: `${binding}src/index.ts` }));
      build.onResolve({ filter: /^(react|react-dom)(\/.*)?$/ }, ({ path }) => ({ path: Bun.resolveSync(path, binding) }));
    },
  }],
});
if (!result.success) {
  for (const log of result.logs) console.error(log);
  process.exitCode = 1;
} else {
  mkdirSync("dist", { recursive: true });
  copyFileSync("index.html", "dist/index.html");
}
