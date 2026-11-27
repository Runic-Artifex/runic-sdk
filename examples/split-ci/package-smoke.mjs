// runic-sdk's check of the split CI example against this checkout's packed packages (the package-consumers job, D082):
// it copies the example, points its NuGet.config at the candidate feed and its frontend's .npmrc at a registry that
// serves the candidate npm archives, moves the example's Runic version to the candidates' when they differ, and runs
// ci/local-run.sh there: the tests and the web bundle as the Linux job, then a win-x64 publish from the bundle with
// neither Node.js nor Bun on PATH, and a publish after a ViewModel change that must stop with RUNICBRIDGE094.
// A copy of the example outside runic-sdk does not need this file.
//
// Usage: bun examples/split-ci/package-smoke.mjs [packages-directory] [--package deb] [--keep]
// The packages directory has nuget/ and npm/ (`bun run pack` writes artifacts/packages).
import { spawn, spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync, appendFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { trainFeedNpm, trainFeedSource } from "../../eng/ci/train-feed.mjs";

const root = fileURLToPath(new URL("../../", import.meta.url));
const example = fileURLToPath(new URL(".", import.meta.url));
const args = process.argv.slice(2);
const keep = args.includes("--keep");
const packageIndex = args.indexOf("--package");
const packageTarget = packageIndex >= 0 ? args[packageIndex + 1] : null;
const packages = resolve(args.find((arg, index) => !arg.startsWith("--") && index !== packageIndex + 1) ?? join(root, "artifacts/packages"));
const nuget = join(packages, "nuget"), npm = join(packages, "npm");
const { version } = JSON.parse(readFileSync(join(root, "eng/workspace.json"), "utf8"));

const nugetIds = readdirSync(nuget).filter(name => name.endsWith(`.${version}.nupkg`)).map(name => name.slice(0, -`.${version}.nupkg`.length));
for (const id of ["dotnet-runic", "Runic.Application.Views.Desktop", "Runic.Desktop.Kestrel", "Runic.Application.Testing"])
  if (!nugetIds.includes(id)) throw new Error(`${id} ${version} is not in ${nuget}; run bun run pack first.`);
const npmArchives = readdirSync(npm).filter(name => name.endsWith(`-${version}.tgz`)).map(name => join(npm, name));
if (!npmArchives.some(file => file.endsWith(`runic-artifex-views-${version}.tgz`)))
  throw new Error(`@runic-artifex/views ${version} is not in ${npm}; run bun run pack first.`);

const temporary = mkdtempSync(join(tmpdir(), "runic-split-ci-"));
const source = join(temporary, "source");
let registry;
try {
  cpSync(example, source, {
    recursive: true,
    filter: path => {
      const parts = relative(example, path).split(/[\\/]/);
      return !parts.some(part => ["bin", "obj", "node_modules", "dist", "artifacts", "generated", "TestResults"].includes(part))
        && parts.join("/") !== "package-smoke.mjs";
    },
  });

  // The example names the Runic release it shipped with; test it with the candidates.
  const pinned = /<RunicVersion>([^<]+)<\/RunicVersion>/.exec(readFileSync(join(source, "Directory.Packages.props"), "utf8"))?.[1];
  if (!pinned) throw new Error("Directory.Packages.props sets no RunicVersion.");
  if (pinned !== version) {
    const replace = (path, update) => writeFileSync(join(source, path), update(readFileSync(join(source, path), "utf8")));
    replace("Directory.Packages.props", text => text.replace(`<RunicVersion>${pinned}</RunicVersion>`, `<RunicVersion>${version}</RunicVersion>`));
    replace(".config/dotnet-tools.json", text => JSON.stringify({ ...JSON.parse(text), tools: { ...JSON.parse(text).tools,
      "dotnet-runic": { ...JSON.parse(text).tools["dotnet-runic"], version } } }, null, 2));
    replace("src/Acme.App/Frontend/package.json", text => {
      const manifest = JSON.parse(text);
      manifest.dependencies["@runic-artifex/views"] = version;
      return `${JSON.stringify(manifest, null, 2)}\n`;
    });
    replace("src/Acme.App/Frontend/package-lock.json", text => {
      const lock = JSON.parse(text);
      lock.packages[""].dependencies["@runic-artifex/views"] = version;
      lock.packages["node_modules/@runic-artifex/views"].version = version;
      return `${JSON.stringify(lock, null, 2)}\n`;
    });
  }

  // The candidates come from the candidate feed only, and everything else from nuget.org (and a train's own feed).
  const train = trainFeedSource();
  writeFileSync(join(source, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear/><add key="runic-candidate" value="${nuget}"/>${train.source}<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/>
    <packageSource key="runic-candidate">${nugetIds.map(id => `<package pattern="${id}"/>`).join("")}</packageSource>${train.mapping}
    <packageSource key="nuget.org"><package pattern="*"/></packageSource>
  </packageSourceMapping>
</configuration>
`);

  const ready = join(temporary, "registry.url");
  const served = [...npmArchives, ...Object.values(trainFeedNpm()).map(item => item.file)];
  registry = spawn(process.execPath, [join(root, "tests/templates/template-npm-registry.mjs"), ready, ...served], { stdio: "inherit" });
  let address;
  for (const deadline = Date.now() + 10_000; !address && Date.now() < deadline && registry.exitCode === null;) {
    address = existsSync(ready) ? readFileSync(ready, "utf8").trim() : "";
    if (!address) await new Promise(done => setTimeout(done, 25));
  }
  if (!address) throw new Error("The candidate npm registry did not start.");
  appendFileSync(join(source, "src/Acme.App/Frontend/.npmrc"), `@runic-artifex:registry=${address}/\n`);

  const run = spawnSync("bash", [join(source, "ci/local-run.sh"), "--source", source, "--work", join(temporary, "run"),
    ...(packageTarget ? ["--package", packageTarget] : []), ...(keep ? ["--keep"] : [])], {
    stdio: "inherit",
    // A package cache of its own: the global one may hold a published package of the candidates' version. A CLI home
    // of its own too: the tool resolver cache (~/.dotnet/toolResolverCache) would otherwise keep dotnet-runic pointing
    // into this run's deleted package cache, and `dotnet tool run` would fail in every other checkout.
    env: { ...process.env, NUGET_PACKAGES: join(temporary, "nuget-packages"), DOTNET_CLI_HOME: join(temporary, "dotnet-home") },
  });
  if (run.status !== 0) throw new Error(`ci/local-run.sh exited with ${run.status ?? run.signal}.`);
} finally {
  registry?.kill();
  if (keep) console.log(`Kept ${temporary}`);
  else rmSync(temporary, { recursive: true, force: true });
}
