import { createHash } from "node:crypto";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { createReadStream, readFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const schema = "runic-w40-mf2-subset-consumer-receipt/1";
const repeatSchema = "runic-w40-mf2-subset-consumer-repeat-receipt/1";
const profile = "runic-mf2-subset/1";
const nugetFeed = process.env.RUNIC_W40_NUGET_FEED && resolve(process.env.RUNIC_W40_NUGET_FEED);
const npmArchive = process.env.RUNIC_W40_NPM_ARCHIVE && resolve(process.env.RUNIC_W40_NPM_ARCHIVE);
const nugetIdentities = ["Runic.Translations", "Runic.Translations.Build", "dotnet-runic-translations"];
const npmIdentity = "@runic-artifex/vite-plugin-runic-translations";

const translationProject = JSON.stringify({
  schemaVersion: 1, catalog: "w40", code: { namespace: "W40.Consumer", className: "W40Text", visibility: "public" },
  baseLocale: "en", locales: ["en", { tag: "de", fallback: "en" }],
  validation: { translationCompleteness: "error", extraLocaleKeys: "error", emptyValues: "error" },
}, null, 2) + "\n";

const message = welcome => `.input {$name :string}\n${welcome}{$name}\n`;
const invalidMf2 = ".input {$name :unsupported}\nHello {$name}\n";
const program = `using System.Text.Json;\nusing W40.Consumer;\nusing Runic.Translations;\nvar en = new W40Text(await W40TextCatalog.CreateManagerAsync("en"));\nvar de = new W40Text(await W40TextCatalog.CreateManagerAsync("de"));\nConsole.WriteLine(JsonSerializer.Serialize(new { catalog = W40TextCatalog.CatalogId, contractFingerprint = W40TextCatalog.ContractFingerprint, runtimeAbiVersion = W40TextCatalog.RuntimeAbiVersion, generatorVersion = W40TextCatalog.GeneratorVersion, locales = new[] { "de", "en" }, messages = new { en = new { welcome = en.greeting_welcome("Ada") }, de = new { welcome = de.greeting_welcome("Ada") } } }));\n`;

const project = version => `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IntermediateOutputPath>obj/</IntermediateOutputPath><TranslationsEmitEsm>true</TranslationsEmitEsm><TranslationsOutputPath>obj/$(TargetFramework)/translations</TranslationsOutputPath></PropertyGroup><ItemGroup><PackageReference Include="Runic.Translations" Version="${version}"/><PackageReference Include="Runic.Translations.Build" Version="${version}" PrivateAssets="all"/></ItemGroup><ItemGroup><TranslationsToolInput Include=".config/dotnet-tools.json"/></ItemGroup></Project>\n`;
const toolManifest = version => JSON.stringify({ version: 1, isRoot: true, tools: { "dotnet-runic-translations": { version, commands: ["runic-translations"] } } }, null, 2) + "\n";
const nugetConfig = feed => `<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="candidate" value="${xml(feed)}"/></packageSources><config><add key="globalPackagesFolder" value=".nuget/packages"/></config></configuration>\n`;

export function verifyReceipt(receipt) {
  const errors = [];
  if (receipt?.schema !== repeatSchema || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push("two deterministic journeys are required");
  const journeys = receipt?.journeys ?? [];
  const first = journeys[0];
  for (const journey of journeys) {
    if (journey?.schema !== schema) { errors.push("journey schema mismatch"); continue; }
    if (!same(journey.isolation, { nugetGlobalPackagesFolder: ".nuget/packages", nugetHttpCachePath: ".nuget/http-cache", dotnetCliHome: ".dotnet", npmCache: ".npm-cache" })) errors.push("cache isolation mismatch");
    if (journey.profile !== profile || journey.manifest?.catalog !== "w40" || !/^sha256:[a-f0-9]{64}$/.test(journey.manifest?.contractFingerprint ?? "") || journey.manifest?.esmAbiVersion !== 3 || journey.manifest?.runtimeAbiVersion !== 1 || journey.manifest?.generatorVersion !== 1) errors.push("catalog contract mismatch");
    if (!same(journey.parity?.locales, ["de", "en"]) || !same(journey.parity?.messages, expectedMessages())) errors.push("C# and ESM parity mismatch");
    if (!same(journey.negativeGates, ["unsupported-mf2-function", "abi-mismatch", "schema-mismatch", "stale-generated-manifest", "forged-generated-manifest"])) errors.push("negative gate evidence mismatch");
    if (!Array.isArray(journey.nugetCandidates) || journey.nugetCandidates.length !== nugetIdentities.length || journey.nugetCandidates.some((value, index) => value?.identity !== nugetIdentities[index] || !value.version || value.source !== "exact-local" || !hash(value.archiveSha256) || !value.contentHash)) errors.push("NuGet provenance mismatch");
    if (journey.npmCandidate?.identity !== npmIdentity || !journey.npmCandidate?.version || journey.npmCandidate?.source !== "exact-local" || !journey.npmCandidate?.integrity || !hash(journey.npmCandidate?.archiveSha256)) errors.push("npm provenance mismatch");
    if (!Array.isArray(journey.phases) || journey.phases.some(phase => phase?.exitCode !== 0) || !["restore", "tool-restore", "build", "npm-install", "parity"].every(name => journey.phases.some(phase => phase.name === name))) errors.push("package journey is incomplete");
  }
  if (journeys.length === 2 && (!same(first.manifest, journeys[1].manifest) || !same(first.parity, journeys[1].parity) || !same(first.nugetCandidates, journeys[1].nugetCandidates) || !same(first.npmCandidate, journeys[1].npmCandidate))) errors.push("journeys are not deterministic");
  return { ok: errors.length === 0, errors };
}

export async function runTwice() {
  requireInputs();
  const journeys = [await runOne(), await runOne()];
  const receipt = { schema: repeatSchema, journeys };
  const report = verifyReceipt(receipt);
  if (!report.ok) throw new Error(report.errors.join("\n"));
  return receipt;
}

async function runOne() {
  const version = await candidateVersion();
  const npm = await npmCandidate();
  const directory = await mkdtemp(join(tmpdir(), "runic-w40-mf2-consumer-"));
  const environment = { ...process.env, DOTNET_CLI_HOME: join(directory, ".dotnet"), NUGET_PACKAGES: join(directory, ".nuget/packages"), NUGET_HTTP_CACHE_PATH: join(directory, ".nuget/http-cache"), npm_config_cache: join(directory, ".npm-cache") };
  const phases = [];
  let registry;
  try {
    registry = await startRegistry(npm.archive);
    await writeFixture(directory, version, registry.url);
    for (const [name, command, args] of [["restore", "dotnet", ["restore", "W40.Consumer.csproj", "--configfile", "NuGet.config", "--no-cache", "--force-evaluate", "--nologo"]], ["tool-restore", "dotnet", ["tool", "restore", "--configfile", "NuGet.config", "--no-cache"]], ["build", "dotnet", ["build", "W40.Consumer.csproj", "--no-restore", "--configuration", "Release", "--nologo"]], ["npm-install", "npm", ["install", "--ignore-scripts"]]]) {
      const result = await run(command, args, directory, environment); phases.push(phase(name, command, args, result)); requireSuccess(name, result);
    }
    const csharp = JSON.parse((await run("dotnet", ["run", "--project", "W40.Consumer.csproj", "--no-build", "--configuration", "Release", "--nologo"], directory, environment)).output);
    const generated = join(directory, "obj/net10.0/translations/w40.esm");
    const manifestPath = join(generated, "web-module-manifest-v1.json");
    const manifest = await generatedManifest(manifestPath, csharp);
    const parity = await esmParity(directory, manifestPath, csharp);
    const negativeGates = await negatives(directory, manifestPath, environment);
    phases.push({ name: "parity", command: "node", args: ["<installed-vite-plugin>", "<generated-manifest>"], exitCode: 0 });
    const nugetCandidates = await nugetProvenance(directory, version);
    const lock = JSON.parse(await readFile(join(directory, "package-lock.json"), "utf8"));
    const installed = lock.packages?.[`node_modules/${npmIdentity}`];
    if (!installed || installed.version !== npm.version || !installed.resolved?.startsWith(registry.url) || !installed.integrity) throw new Error("npm provenance failed closed");
    return { schema, profile, isolation: { nugetGlobalPackagesFolder: ".nuget/packages", nugetHttpCachePath: ".nuget/http-cache", dotnetCliHome: ".dotnet", npmCache: ".npm-cache" }, manifest, parity, negativeGates, nugetCandidates, npmCandidate: { identity: npmIdentity, version: npm.version, source: "exact-local", integrity: installed.integrity, archiveSha256: await sha256(npm.archive) }, phases };
  } finally { registry?.close(); await rm(directory, { recursive: true, force: true }); }
}

async function writeFixture(directory, version, registry) {
  await Promise.all([
    writeFile(join(directory, "NuGet.config"), nugetConfig(nugetFeed)), writeFile(join(directory, "W40.Consumer.csproj"), project(version)), writeFile(join(directory, "Program.cs"), program),
    writeFile(join(directory, "package.json"), JSON.stringify({ private: true, type: "module", dependencies: { [npmIdentity]: (await npmCandidate()).version } }, null, 2) + "\n"), writeFile(join(directory, ".npmrc"), `@runic-artifex:registry=${registry}\n`),
  ]);
  for (const [path, content] of [["translations/runic.json", translationProject], ["translations/en/greeting_welcome.mf2", message("Hello ")], ["translations/de/greeting_welcome.mf2", message("Hallo ")], ["invalid-translations/runic.json", translationProject], ["invalid-translations/en/greeting_welcome.mf2", invalidMf2], ["invalid-translations/de/greeting_welcome.mf2", message("Hallo ")], [".config/dotnet-tools.json", toolManifest(version)]]) {
    const target = join(directory, path); await mkdir(dirname(target), { recursive: true }); await writeFile(target, content);
  }
}

async function generatedManifest(path, csharp) {
  const manifest = JSON.parse(await readFile(path, "utf8"));
  if (manifest.webModuleManifestVersion !== 1 || manifest.esmAbiVersion !== 3 || manifest.catalog !== csharp.catalog || manifest.contractFingerprint !== csharp.contractFingerprint || !Array.isArray(manifest.assets)) throw new Error("generated manifest contract failed closed");
  for (const asset of manifest.assets) { const content = await readFile(join(dirname(path), asset.path)); if (asset.byteLength !== content.byteLength || asset.sha256 !== await sha256(content)) throw new Error(`generated asset mismatch: ${asset.path}`); }
  return { catalog: manifest.catalog, contractFingerprint: manifest.contractFingerprint, esmAbiVersion: manifest.esmAbiVersion, runtimeAbiVersion: csharp.runtimeAbiVersion, generatorVersion: csharp.generatorVersion, sha256: await sha256(path) };
}

async function esmParity(directory, manifestPath, csharp) {
  const plugin = await import(pathToFileURL(join(directory, "node_modules", ...npmIdentity.split("/"), "dist/index.js")).href);
  const adapter = plugin.runicTranslations({ manifest: manifestPath }); await adapter.buildStart.call({ addWatchFile() {} });
  const manifest = JSON.parse(await readFile(manifestPath, "utf8")); const root = dirname(manifestPath);
  const [messages, runtime] = await Promise.all([import(pathToFileURL(join(root, manifest.entrypoints.messages)).href), import(pathToFileURL(join(root, manifest.entrypoints.runtime)).href)]);
  if (runtime.contractFingerprint !== csharp.contractFingerprint || runtime.esmAbiVersion !== 3 || !same(runtime.locales, csharp.locales)) throw new Error("generated ESM contract mismatch");
  const actual = { locales: runtime.locales, messages: { en: { welcome: messages.m.greeting_welcome({ name: "Ada" }, { locale: "en" }) }, de: { welcome: messages.m.greeting_welcome({ name: "Ada" }, { locale: "de" }) } } };
  if (!same(actual.messages, csharp.messages)) throw new Error("C# and ESM messages diverged"); return actual;
}

async function negatives(directory, manifestPath, environment) {
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  const messagesPath = join(dirname(manifestPath), manifest.entrypoints.messages); const originalManifest = JSON.stringify(manifest); const originalMessages = await readFile(messagesPath);
  const rejects = async (name, mutate) => { await mutate(); try { const plugin = await import(pathToFileURL(join(directory, "node_modules", ...npmIdentity.split("/"), "dist/index.js")).href); await assertRejects(() => plugin.runicTranslations({ manifest: manifestPath }).buildStart.call({ addWatchFile() {} })); return name; } finally { await writeFile(manifestPath, originalManifest); await writeFile(messagesPath, originalMessages); } };
  const unsupported = await run("dotnet", ["tool", "run", "runic-translations", "--", "validate", "--project", "invalid-translations"], directory, environment); if (unsupported.exitCode === 0 || !unsupported.output.includes("RTR0041")) throw new Error("unsupported MF2 function was accepted");
  return ["unsupported-mf2-function", await rejects("abi-mismatch", async () => writeFile(manifestPath, JSON.stringify({ ...manifest, esmAbiVersion: 99 }))), await rejects("schema-mismatch", async () => writeFile(manifestPath, JSON.stringify({ ...manifest, webModuleManifestVersion: 99 }))), await rejects("stale-generated-manifest", async () => writeFile(messagesPath, Buffer.concat([originalMessages, Buffer.from("// stale\n")]))) , await rejects("forged-generated-manifest", async () => writeFile(manifestPath, JSON.stringify({ ...manifest, contractFingerprint: `sha256:${"0".repeat(64)}` })))] ;
}

async function nugetProvenance(directory, version) {
  const assets = JSON.parse(await readFile(join(directory, "obj/project.assets.json"), "utf8"));
  return Promise.all(nugetIdentities.map(async identity => {
    const archive = join(nugetFeed, `${identity}.${version}.nupkg`);
    if (identity === "dotnet-runic-translations") return { identity, version, source: "exact-local", contentHash: await sha512(archive), archiveSha256: await sha256(archive) };
    const library = Object.entries(assets.libraries).find(([key]) => key.toLowerCase() === `${identity}/${version}`.toLowerCase())?.[1]; const metadata = JSON.parse(await readFile(join(directory, ".nuget/packages", identity.toLowerCase(), version.toLowerCase(), ".nupkg.metadata"), "utf8")); if (!library || metadata.source !== nugetFeed || metadata.contentHash !== library.sha512) throw new Error(`NuGet provenance failed closed for ${identity}`); return { identity, version, source: "exact-local", contentHash: metadata.contentHash, archiveSha256: await sha256(archive) };
  }));
}

async function candidateVersion() { const files = await readdir(nugetFeed); const match = files.map(name => /^Runic\.Translations\.([0-9][0-9A-Za-z.-]*)\.nupkg$/.exec(name)).find(Boolean); if (!match || !nugetIdentities.every(identity => files.includes(`${identity}.${match[1]}.nupkg`))) throw new Error("RUNIC_W40_NUGET_FEED must contain one exact local Runic Translations candidate family."); return match[1]; }
async function npmCandidate() { const manifest = JSON.parse(await tarJson(npmArchive)); if (manifest.name !== npmIdentity || !manifest.version) throw new Error("RUNIC_W40_NPM_ARCHIVE is not the exact Runic translations Vite candidate."); return { archive: npmArchive, version: manifest.version }; }
async function startRegistry(archive) { const manifest = JSON.parse(await tarJson(archive)); const server = createServer((request, response) => { const path = new URL(request.url ?? "/", "http://127.0.0.1").pathname; const archivePath = "/archive/" + basename(archive); if (decodeURIComponent(path.slice(1)) === manifest.name) { const tarball = `http://127.0.0.1:${server.address().port}${archivePath}`; const integrity = "sha512-" + createHash("sha512").update(readFileSync(archive)).digest("base64"); response.writeHead(200, { "content-type": "application/json" }); return response.end(JSON.stringify({ name: manifest.name, "dist-tags": { latest: manifest.version }, versions: { [manifest.version]: { ...manifest, dist: { tarball, integrity } } } })); } if (path === archivePath) { response.writeHead(200); return createReadStream(archive).pipe(response); } response.writeHead(404); response.end(); }); await new Promise((ok, bad) => { server.once("error", bad); server.listen(0, "127.0.0.1", ok); }); return { url: `http://127.0.0.1:${server.address().port}`, close: () => server.close() }; }
async function tarJson(archive) { const result = await run("tar", ["-xOf", archive, "package/package.json"], here); requireSuccess("read npm candidate", result); return result.output; }
async function run(command, args, cwd, environment = process.env) { return new Promise(resolveResult => { const child = spawn(command, args, { cwd, env: environment, stdio: ["ignore", "pipe", "pipe"] }); let output = ""; child.stdout.on("data", value => output += value); child.stderr.on("data", value => output += value); child.on("close", exitCode => resolveResult({ exitCode: exitCode ?? 1, output })); child.on("error", error => resolveResult({ exitCode: 1, output: String(error) })); }); }
function phase(name, command, args, result) { return { name, command, args, exitCode: result.exitCode }; }
function requireSuccess(name, result) { if (result.exitCode !== 0) throw new Error(`${name} failed:\n${result.output}`); }
function expectedMessages() { return { en: { welcome: "Hello Ada" }, de: { welcome: "Hallo Ada" } }; }
function hash(value) { return typeof value === "string" && /^[a-f0-9]{64}$/.test(value); }
async function sha256(value) { const content = typeof value === "string" ? await readFile(value) : value; return createHash("sha256").update(content).digest("hex"); }
async function sha512(value) { const content = typeof value === "string" ? await readFile(value) : value; return "sha512-" + createHash("sha512").update(content).digest("base64"); }
function xml(value) { return value.replaceAll("&", "&amp;").replaceAll("\"", "&quot;"); }
function same(left, right) { return JSON.stringify(left) === JSON.stringify(right); }
async function assertRejects(action) { try { await action(); } catch { return; } throw new Error("expected generated manifest validation to fail"); }
function requireInputs() { if (!nugetFeed || !npmArchive) throw new Error("RUNIC_W40_NUGET_FEED and RUNIC_W40_NPM_ARCHIVE must name exact local candidates."); }

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [command, receiptPath] = process.argv.slice(2);
  if (command === "run-twice" && !receiptPath) process.stdout.write(JSON.stringify(await runTwice(), null, 2) + "\n");
  else if (command === "verify-twice" && receiptPath) { const report = verifyReceipt(JSON.parse(await readFile(receiptPath, "utf8"))); if (!report.ok) throw new Error(report.errors.join("\n")); }
  else throw new Error("Usage: node eng/current-mf2-subset-consumer/verify.mjs run-twice | verify-twice <receipt.json>");
}
