#!/usr/bin/env node
// Packages the application for one platform: an MSIX package on Windows, a signed .app bundle in a disk image on
// macOS, a Flatpak bundle on Linux. Run it on that platform from the project directory, with Node 22 or later (or
// Bun):
//
//   node packaging/package.mjs windows [--publisher "CN=Example Ltd"] [--certificate app.pfx | --thumbprint <sha1>]
//   node packaging/package.mjs macos [--sign "Developer ID Application: Example Ltd (TEAMID)"] [--notary-profile <name>]
//   node packaging/package.mjs flatpak [--gpg-sign <key id>]
//
// It reads the application's identity from the project file (ApplicationId, ApplicationTitle, Version,
// RunicApplicationUrlSchemes, RunicApplicationPublisher, Description, PackageProjectUrl), publishes it with NativeAOT for the platform,
// fills the {{...}} fields of the manifests next to this script and writes the package to artifacts/package. Without
// signing options the package is unsigned (Windows), ad-hoc signed (macOS) or unsigned (Flatpak): install it to test
// it; sign it to ship it. See the README's "Package it" section.
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

export function parseArguments(argv) {
  const [target, ...rest] = argv;
  const options = { target, project: undefined, output: undefined, rid: undefined };
  for (let index = 0; index < rest.length; index++) {
    const name = rest[index];
    if (!name.startsWith("--")) throw new Error(`Unexpected argument '${name}'.`);
    const value = rest[index + 1];
    if (value === undefined || value.startsWith("--")) throw new Error(`${name} needs a value.`);
    options[name.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = value;
    index++;
  }
  if (!["windows", "macos", "flatpak"].includes(target))
    throw new Error("Usage: node packaging/package.mjs <windows|macos|flatpak> [options]");
  return options;
}

// Every platform accepts this ID: a D-Bus and Flatpak name, a bundle identifier and an MSIX package name.
export function checkApplicationId(id) {
  if (!/^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+$/.test(id) || id.length > 50)
    throw new Error(`ApplicationId '${id}' must be a reverse-DNS name of ASCII letters and digits, each part starting with a letter, at most 50 characters (org.example.Notes).`);
  return id;
}

// A URL scheme as RFC 3986 allows it, in lower case, which every platform matches.
export function urlSchemes(value) {
  const schemes = (value ?? "").split(/[;,\s]+/).filter(Boolean).map(scheme => scheme.toLowerCase());
  for (const scheme of schemes)
    if (!/^[a-z][a-z0-9+.-]*$/.test(scheme) || ["http", "https", "file"].includes(scheme))
      throw new Error(`RunicApplicationUrlSchemes has '${scheme}', which is not a URL scheme an application can register.`);
  return schemes;
}

// MSIX and Info.plist need numeric versions: 1.2.3-preview.4 becomes 1.2.3 and 1.2.3.0.
export function numericVersion(version, parts) {
  const numbers = (version ?? "1.0.0").split(/[-+]/)[0].split(".").map(part => Number.parseInt(part, 10) || 0);
  while (numbers.length < parts) numbers.push(0);
  return numbers.slice(0, parts).join(".");
}

export const escapeXml = text => String(text).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

// Replaces {{Name}} with fields[Name]; a field the template uses but the caller does not supply is an error.
export function fill(template, fields) {
  return template.replace(/\{\{([A-Za-z]+)\}\}/g, (_, name) => {
    if (!(name in fields)) throw new Error(`The manifest uses {{${name}}}, which package.mjs does not fill.`);
    return fields[name];
  });
}

// The fields every manifest can use, with the platform's URL scheme entries.
export function manifestFields(identity, target, rid = "") {
  const xml = Object.fromEntries(Object.entries(identity).map(([key, value]) => [key, escapeXml(value)]));
  const schemes = identity.urlSchemes;
  const fields = {
    ApplicationId: identity.id,
    ApplicationName: xml.name,
    Description: xml.description,
    Executable: identity.executable,
    Version: identity.version,
  };
  if (target === "windows") {
    fields.Publisher = xml.publisher;
    fields.Architecture = rid.endsWith("arm64") ? "arm64" : "x64";
    fields.PackageVersion = numericVersion(identity.version, 4);
    fields.Extensions = schemes.length === 0 ? "" : `
      <Extensions>${schemes.map(scheme => `
        <uap:Extension Category="windows.protocol">
          <uap:Protocol Name="${scheme}">
            <uap:DisplayName>${xml.name}</uap:DisplayName>
          </uap:Protocol>
        </uap:Extension>`).join("")}
      </Extensions>`;
  } else if (target === "macos") {
    fields.BundleVersion = numericVersion(identity.version, 3);
    fields.UrlSchemes = schemes.length === 0 ? "" : `
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>${identity.id}</string>
      <key>CFBundleURLSchemes</key><array>${schemes.map(scheme => `<string>${scheme}</string>`).join("")}</array>
    </dict>
  </array>`;
  } else {
    fields.ReleaseDate = new Date().toISOString().slice(0, 10);
    fields.Homepage = xml.homepage;
    fields.MimeTypes = schemes.length === 0 ? "" : `MimeType=${schemes.map(scheme => `x-scheme-handler/${scheme};`).join("")}\n`;
    // The reverse-DNS prefix of the ID, as AppStream names developers.
    fields.DeveloperId = identity.id.split(".").slice(0, -1).join(".");
  }
  return fields;
}

function run(command, args, options = {}) {
  console.log(`> ${command} ${args.join(" ")}`);
  const result = spawnSync(command, args, { stdio: options.capture ? ["ignore", "pipe", "inherit"] : "inherit", encoding: "utf8", ...options });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} exited with ${result.status ?? result.signal}.`);
  return result.stdout;
}

function findProject(option) {
  if (option) return resolve(option);
  const directory = resolve(here, "..");
  const projects = readdirSync(directory).filter(name => name.endsWith(".csproj"));
  if (projects.length !== 1) throw new Error(`Pass --project: ${directory} has ${projects.length} project files.`);
  return join(directory, projects[0]);
}

function readIdentity(project, options) {
  const names = ["ApplicationId", "ApplicationTitle", "Version", "RunicApplicationUrlSchemes", "RunicApplicationPublisher", "Description",
    "PackageProjectUrl", "AssemblyName"];
  const output = run("dotnet", ["msbuild", project, ...names.map(name => `-getProperty:${name}`)], { capture: true });
  const properties = JSON.parse(output).Properties;
  const id = checkApplicationId(properties.ApplicationId ?? "");
  return {
    id,
    name: properties.ApplicationTitle || properties.AssemblyName,
    version: properties.Version || "1.0.0",
    urlSchemes: urlSchemes(properties.RunicApplicationUrlSchemes),
    publisher: options.publisher || properties.RunicApplicationPublisher || "CN=Example",
    description: properties.Description || properties.ApplicationTitle || properties.AssemblyName,
    homepage: properties.PackageProjectUrl || "https://example.com",
    executable: properties.AssemblyName,
  };
}

function publish(project, rid, directory) {
  rmSync(directory, { recursive: true, force: true });
  run("dotnet", ["publish", project, "-c", "Release", "-r", rid, "-p:PublishAot=true", "-p:PublishDocumentationFiles=false",
    "-p:PublishReferencesDocumentationFiles=false", "-o", directory]);
  // Debug symbols stay out of the package; keep them per release from the publish folder for crash analysis.
  for (const name of readdirSync(directory))
    if (/\.(pdb|dbg)$/.test(name) || name.endsWith(".dSYM")) rmSync(join(directory, name), { recursive: true, force: true });
}

const template = (target, name) => readFileSync(join(here, target, name), "utf8");

// Windows: an MSIX package of the NativeAOT publish folder, a full-trust desktop application.
function packageWindows(project, identity, options, output) {
  const rid = options.rid ?? (process.arch === "arm64" ? "win-arm64" : "win-x64");
  const layout = join(output, "msix");
  publish(project, rid, layout);
  const fields = manifestFields(identity, "windows", rid);
  writeFileSync(join(layout, "AppxManifest.xml"), fill(template("windows", "AppxManifest.xml"), fields));
  mkdirSync(join(layout, "Assets"), { recursive: true });
  cpSync(join(here, "..", "icons", "icon.png"), join(layout, "Assets", "icon.png"));
  const msix = join(output, `${identity.id}_${fields.PackageVersion}_${fields.Architecture}.msix`);
  run(windowsKitTool("makeappx.exe"), ["pack", "/o", "/d", layout, "/p", msix]);
  const timestamp = options.timestampUrl ? ["/tr", options.timestampUrl, "/td", "SHA256"] : [];
  if (options.certificate) {
    const password = process.env.RUNIC_SIGNING_PASSWORD ? ["/p", process.env.RUNIC_SIGNING_PASSWORD] : [];
    run(windowsKitTool("signtool.exe"), ["sign", "/fd", "SHA256", "/f", resolve(options.certificate), ...password, ...timestamp, msix]);
  } else if (options.thumbprint) {
    run(windowsKitTool("signtool.exe"), ["sign", "/fd", "SHA256", "/sha1", options.thumbprint, ...timestamp, msix]);
  } else {
    console.log(`The package is unsigned: sign it with signtool (its Publisher is ${identity.publisher}) before installing it.`);
  }
  return msix;
}

// makeappx and signtool come with the Windows SDK, which puts them in a versioned folder rather than on PATH.
function windowsKitTool(name) {
  const which = spawnSync("where", [name], { encoding: "utf8" });
  if (which.status === 0) return which.stdout.split(/\r?\n/)[0];
  const root = join(process.env["ProgramFiles(x86)"] ?? "C:\\Program Files (x86)", "Windows Kits", "10", "bin");
  const versions = existsSync(root) ? readdirSync(root).filter(name => /^10\./.test(name)).sort((a, b) =>
    b.localeCompare(a, undefined, { numeric: true })) : [];
  const architecture = process.arch === "arm64" ? "arm64" : "x64";
  for (const version of versions) {
    const tool = join(root, version, architecture, name);
    if (existsSync(tool)) return tool;
  }
  throw new Error(`${name} was not found. Install the Windows SDK (it provides makeappx and signtool).`);
}

// macOS: an .app bundle (the executable in Contents/MacOS, the frontend and icon in Contents/Resources), signed with
// the hardened runtime, in a disk image; notarized and stapled when a notary profile is given.
function packageMacOS(project, identity, options, output) {
  const rids = (options.rid ?? "osx-arm64").split(",");
  const app = join(output, `${identity.name}.app`);
  rmSync(app, { recursive: true, force: true });
  mkdirSync(join(app, "Contents", "MacOS"), { recursive: true });
  mkdirSync(join(app, "Contents", "Resources"), { recursive: true });
  const published = rids.map(rid => {
    const directory = join(output, `publish-${rid}`);
    publish(project, rid, directory);
    return directory;
  });
  const executable = join(app, "Contents", "MacOS", identity.executable);
  if (published.length === 1) cpSync(join(published[0], identity.executable), executable);
  else run("lipo", ["-create", "-output", executable, ...published.map(directory => join(directory, identity.executable))]);
  // Apple expects only code in Contents/MacOS; Runic finds the frontend in Contents/Resources/www.
  if (existsSync(join(published[0], "www"))) cpSync(join(published[0], "www"), join(app, "Contents", "Resources", "www"), { recursive: true });
  for (const name of readdirSync(published[0]))
    if (name !== identity.executable && name !== "www")
      cpSync(join(published[0], name), join(app, "Contents", "MacOS", name), { recursive: true });
  icns(join(here, "..", "icons", "icon.png"), join(app, "Contents", "Resources", "icon.icns"), output);
  writeFileSync(join(app, "Contents", "Info.plist"), fill(template("macos", "Info.plist"), manifestFields(identity, "macos")));

  const identityName = options.sign ?? "-";
  const timestamp = identityName === "-" ? [] : ["--timestamp"];
  run("codesign", ["--force", "--options", "runtime", ...timestamp, "--entitlements", join(here, "macos", "entitlements.plist"),
    "--sign", identityName, app]);
  run("codesign", ["--verify", "--strict", "--verbose=2", app]);

  const staging = join(output, "dmg");
  rmSync(staging, { recursive: true, force: true });
  mkdirSync(staging);
  run("ditto", [app, join(staging, basename(app))]);
  symlinkSync("/Applications", join(staging, "Applications"));
  const dmg = join(output, `${identity.name}-${identity.version}.dmg`);
  rmSync(dmg, { force: true });
  run("hdiutil", ["create", "-volname", identity.name, "-srcfolder", staging, "-format", "UDZO", "-ov", dmg]);
  rmSync(staging, { recursive: true, force: true });
  if (identityName !== "-") run("codesign", ["--force", "--timestamp", "--sign", identityName, dmg]);
  if (options.notaryProfile) {
    if (identityName === "-") throw new Error("--notary-profile needs --sign with a Developer ID Application identity.");
    run("xcrun", ["notarytool", "submit", dmg, "--keychain-profile", options.notaryProfile, "--wait"]);
    run("xcrun", ["stapler", "staple", dmg]);
  } else {
    console.log("The disk image is not notarized: Gatekeeper blocks it on other Macs until it is (--notary-profile).");
  }
  return dmg;
}

// An .icns from the 512 px PNG: iconutil reads an .iconset folder of the standard sizes.
function icns(png, destination, output) {
  const iconset = join(output, "icon.iconset");
  rmSync(iconset, { recursive: true, force: true });
  mkdirSync(iconset);
  for (const size of [16, 32, 128, 256, 512]) {
    run("sips", ["-z", `${size}`, `${size}`, png, "--out", join(iconset, `icon_${size}x${size}.png`)], { capture: true });
    if (size <= 256)
      run("sips", ["-z", `${size * 2}`, `${size * 2}`, png, "--out", join(iconset, `icon_${size}x${size}@2x.png`)], { capture: true });
  }
  run("iconutil", ["-c", "icns", iconset, "-o", destination]);
  rmSync(iconset, { recursive: true, force: true });
}

// Linux: a Flatpak bundle on the GNOME runtime, which provides GTK 4 and WebKitGTK 6, from the NativeAOT publish folder.
function packageFlatpak(project, identity, options, output) {
  const rid = options.rid ?? (process.arch === "arm64" ? "linux-arm64" : "linux-x64");
  const stage = join(output, "flatpak-source");
  rmSync(stage, { recursive: true, force: true });
  publish(project, rid, join(stage, "publish"));
  const fields = manifestFields(identity, "flatpak");
  writeFileSync(join(stage, `${identity.id}.desktop`), fill(template("linux", "app.desktop"), fields));
  writeFileSync(join(stage, `${identity.id}.metainfo.xml`), fill(template("linux", "app.metainfo.xml"), fields));
  cpSync(join(here, "..", "icons", "icon.png"), join(stage, "icon.png"));
  cpSync(join(here, "..", "icons", "icon.svg"), join(stage, "icon.svg"));
  const manifest = join(output, `${identity.id}.yml`);
  writeFileSync(manifest, fill(template("linux", "flatpak.yml"), fields));
  for (const file of [`${identity.id}.desktop`]) run("desktop-file-validate", [join(stage, file)]);
  run("appstreamcli", ["validate", "--no-net", "--explain", join(stage, `${identity.id}.metainfo.xml`)]);

  const sign = options.gpgSign ? [`--gpg-sign=${options.gpgSign}`] : [];
  run("flatpak-builder", ["--user", "--install-deps-from=flathub", "--force-clean", "--disable-rofiles-fuse",
    `--state-dir=${join(output, ".flatpak-builder")}`, `--repo=${join(output, "repo")}`, ...sign, join(output, "build"), manifest]);
  const bundle = join(output, `${identity.id}.flatpak`);
  run("flatpak", ["build-bundle", ...sign, join(output, "repo"), bundle, identity.id]);
  rmSync(join(output, "build"), { recursive: true, force: true });
  if (!options.gpgSign) console.log("The repository and bundle are unsigned (--gpg-sign signs them).");
  return bundle;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const options = parseArguments(process.argv.slice(2));
    const project = findProject(options.project);
    const identity = readIdentity(project, options);
    const output = resolve(options.output ?? join(dirname(project), "artifacts", "package", options.target));
    mkdirSync(output, { recursive: true });
    const result = { windows: packageWindows, macos: packageMacOS, flatpak: packageFlatpak }[options.target](project, identity, options, output);
    console.log(`Packaged ${identity.id} ${identity.version}: ${result}`);
  } catch (error) {
    console.error(`error: ${error.message}`);
    process.exit(1);
  }
}
