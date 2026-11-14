import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { createReadStream, readFileSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { basename, resolve } from "node:path";
import { isIndependentRunicPackage } from "../../eng/release/template-locks.mjs";

// The scope's registry serves the candidate archives. Runic packages that another Runic repository releases on its own,
// such as the Translations Vite plugin of a --translations project, come from npm unless an archive here (a train
// feed's candidate) provides them; any other unknown Runic package is not found.
const upstream = "https://registry.npmjs.org";

const [readyFile, ...archives] = process.argv.slice(2);
if (!readyFile || archives.length === 0) {
  throw new Error("Usage: template-npm-registry.mjs <ready-file> <npm-archive>...");
}

const packages = new Map();
for (const archive of archives) {
  const absoluteArchive = resolve(archive);
  const manifest = JSON.parse(execFileSync("tar", ["-xOf", absoluteArchive, "package/package.json"], { encoding: "utf8" }));
  if (typeof manifest.name !== "string" || typeof manifest.version !== "string" || !manifest.name.startsWith("@runic-artifex/")) {
    throw new Error(`Expected a Runic Artifex archive, found ${basename(absoluteArchive)}.`);
  }
  packages.set(manifest.name, {
    archive: absoluteArchive,
    integrity: `sha512-${createHash("sha512").update(readFileSync(absoluteArchive)).digest("base64")}`,
    manifest,
  });
}

// The independently released package a request names: its metadata (/@scope%2fname) or a tarball (/@scope/name/-/...).
function independentPackage(path) {
  const decoded = decodeURIComponent(path.slice(1));
  const name = /^(@runic-artifex\/[^/]+)(?:\/-\/[^/]+\.tgz)?$/.exec(decoded)?.[1];
  return name && isIndependentRunicPackage(name) && !packages.has(name) ? name : undefined;
}

async function proxy(request, response, name, path) {
  const tarball = path.includes("/-/");
  const target = tarball ? `${upstream}/${name}/-/${basename(decodeURIComponent(path))}` : `${upstream}/${name.replace("/", "%2f")}`;
  try {
    const answer = await fetch(target, { headers: { accept: request.headers.accept ?? "application/json" } });
    let body = Buffer.from(await answer.arrayBuffer());
    if (!tarball && answer.ok) {
      // First-party packages are exempt from the release-age rule (D038), as the candidates are: without publish
      // times, npm's --before (eng/dependencies/update-template-locks.mjs) does not filter them.
      const metadata = JSON.parse(body.toString("utf8"));
      delete metadata.time;
      body = Buffer.from(JSON.stringify(metadata));
    }
    response.writeHead(answer.status, { "content-type": tarball ? "application/octet-stream" : "application/json" });
    response.end(body);
  } catch (error) {
    response.writeHead(502);
    response.end(String(error));
  }
}

const server = createServer((request, response) => {
  const path = new URL(request.url ?? "/", "http://127.0.0.1").pathname;
  const independent = independentPackage(path);
  if (independent) {
    void proxy(request, response, independent, path);
    return;
  }
  for (const [name, entry] of packages) {
    const archivePath = `/archives/${basename(entry.archive)}`;
    const packageBaseName = name.slice(name.lastIndexOf("/") + 1);
    const conventionalArchivePath = `/${name}/-/${packageBaseName}-${entry.manifest.version}.tgz`;
    if (decodeURIComponent(path.slice(1)) === name) {
      const tarball = `http://127.0.0.1:${server.address().port}${archivePath}`;
      const metadata = {
        name,
        "dist-tags": { latest: entry.manifest.version },
        versions: {
          [entry.manifest.version]: {
            ...entry.manifest,
            dist: { integrity: entry.integrity, tarball },
          },
        },
      };
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify(metadata));
      return;
    }
    if (path === archivePath || decodeURIComponent(path) === conventionalArchivePath) {
      response.writeHead(200, { "content-type": "application/octet-stream" });
      createReadStream(entry.archive).pipe(response);
      return;
    }
  }
  response.writeHead(404);
  response.end();
});

server.listen(0, "127.0.0.1", () => {
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("The test registry did not bind a loopback port.");
  writeFileSync(readyFile, `http://127.0.0.1:${address.port}\n`, "utf8");
});

process.on("SIGTERM", () => server.close(() => process.exit(0)));
process.on("SIGINT", () => server.close(() => process.exit(0)));
