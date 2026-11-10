// Writes the prebuilt runic-views catalog of this package: the sources of Runic.Application.Views.Translations (one
// catalog for the NuGet and the npm package) to dist/catalog, which runicTranslations.project names for an application's
// Vite build, and their ESM modules to dist/translations, with the runic-translations tool that .config/dotnet-tools.json
// pins. --if-missing keeps a catalog that is already built, such as the one CI restores from its build job.
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const root = resolve(here, "../../..");
const sources = resolve(root, "packages/dotnet/Runic.Application.Views.Translations/translations");
const catalog = join(here, "dist/catalog");
const output = join(here, "dist/translations");
const manifest = join(output, "runic-views.esm-v5/web-module-manifest-v3.json");

if (process.argv.includes("--if-missing") && existsSync(manifest) && existsSync(join(catalog, "runic.json"))) process.exit(0);

rmSync(catalog, { recursive: true, force: true });
rmSync(output, { recursive: true, force: true });
mkdirSync(catalog, { recursive: true });
for (const file of readdirSync(sources).filter(name => name === "runic.json" || name.endsWith(".rmf2")))
  cpSync(join(sources, file), join(catalog, file));

function run(args) {
  const result = spawnSync("dotnet", args, { cwd: root, stdio: "inherit" });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`dotnet ${args.join(" ")} exited with ${result.status ?? result.signal}`);
}
run(["tool", "restore"]);
run(["tool", "run", "runic-translations", "--", "generate", "--project", catalog, "--output", output, "--emit-esm"]);
if (!existsSync(manifest)) throw new Error(`runic-translations wrote no ${manifest}`);
