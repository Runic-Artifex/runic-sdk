import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";
import { root } from "../../eng/run.mjs";
import { checkApplicationId, fill, manifestFields, numericVersion, parseArguments, urlSchemes }
  from "../../tools/Runic.Application.Templates/content/runic-app/packaging/package.mjs";

// The runic-app template's packaging/package.mjs: its inputs, and the manifests it fills. The packaging CI job builds,
// installs and opens the packages themselves on each platform.
const packaging = resolve(root, "tools/Runic.Application.Templates/content/runic-app/packaging");
const manifests = {
  windows: ["windows/AppxManifest.xml"],
  macos: ["macos/Info.plist"],
  flatpak: ["linux/app.desktop", "linux/app.metainfo.xml", "linux/flatpak.yml"],
};
const identity = schemes => ({
  id: "org.example.Notes", name: "Notes & Co", version: "2.1.0-preview.3", urlSchemes: schemes,
  publisher: "CN=Example Ltd, O=Example Ltd", description: "Notes <for> everyone", homepage: "https://example.org/notes?a=1&b=2",
  executable: "Notes",
});

function parsesAsXml(text, name) {
  const result = spawnSync("python3", ["-c", "import sys, xml.etree.ElementTree as e; e.fromstring(sys.stdin.read())"], { input: text, encoding: "utf8" });
  assert.equal(result.status, 0, `${name} is not well-formed XML: ${result.stderr}\n${text}`);
}

test("package.mjs reads its target and options", () => {
  assert.deepEqual(parseArguments(["windows", "--certificate", "app.pfx", "--timestamp-url", "http://ts"]),
    { target: "windows", project: undefined, output: undefined, rid: undefined, certificate: "app.pfx", timestampUrl: "http://ts" });
  assert.equal(parseArguments(["macos", "--notary-profile", "notary"]).notaryProfile, "notary");
  assert.throws(() => parseArguments(["android"]), /Usage/);
  assert.throws(() => parseArguments(["flatpak", "--gpg-sign"]), /needs a value/);
  assert.throws(() => parseArguments(["flatpak", "extra"]), /Unexpected argument/);
});

test("package.mjs accepts only IDs and schemes every platform registers", () => {
  for (const id of ["org.example.Notes", "com.example.App2048Game", "a.b"]) assert.equal(checkApplicationId(id), id);
  for (const id of ["Notes", "org.example.my-notes", "org.example.2048", "org..example", "org.example.Notes_App", `org.example.${"a".repeat(40)}`])
    assert.throws(() => checkApplicationId(id), /RunicApplicationId/, id);
  assert.deepEqual(urlSchemes(" Org.Example.Notes; notes+x ,"), ["org.example.notes", "notes+x"]);
  assert.deepEqual(urlSchemes(""), []);
  for (const scheme of ["https", "file", "1notes", "no_tes"]) assert.throws(() => urlSchemes(scheme), /RunicApplicationUrlSchemes/, scheme);
});

test("package.mjs makes numeric package versions", () => {
  assert.equal(numericVersion("2.1.0-preview.3", 4), "2.1.0.0");
  assert.equal(numericVersion("1.2", 3), "1.2.0");
  assert.equal(numericVersion("1.2.3.4+build", 3), "1.2.3");
  assert.equal(numericVersion(undefined, 4), "1.0.0.0");
});

test("package.mjs fills every field of its manifests, escaped, with and without URL schemes", () => {
  assert.throws(() => fill("{{Unknown}}", {}), /does not fill/);
  for (const [target, files] of Object.entries(manifests))
    for (const schemes of [[], ["org.example.notes", "notes-x"]]) {
      const fields = manifestFields(identity(schemes), target);
      for (const file of files) {
        const text = fill(readFileSync(resolve(packaging, file), "utf8"), fields);
        assert.doesNotMatch(text, /\{\{|\}\}/, file);
        if (file.endsWith(".xml") || file.endsWith(".plist")) {
          parsesAsXml(text, file);
          assert.match(text, /Notes &amp; Co/, file);
        }
        if (file === "windows/AppxManifest.xml") {
          assert.match(text, /Version="2\.1\.0\.0"/);
          assert.match(text, /Publisher="CN=Example Ltd, O=Example Ltd"/);
          assert.equal((text.match(/<uap:Protocol Name="/g) ?? []).length, schemes.length);
          assert.equal(/<Extensions>/.test(text), schemes.length > 0, "an empty Extensions element is invalid");
        }
        if (file === "macos/Info.plist") {
          assert.match(text, /<key>CFBundleShortVersionString<\/key><string>2\.1\.0<\/string>/);
          assert.equal(/CFBundleURLSchemes<\/key><array><string>org\.example\.notes<\/string><string>notes-x<\/string>/.test(text), schemes.length > 0);
        }
        if (file === "linux/app.desktop") {
          assert.match(text, /^Exec=Notes %U$/m);
          assert.equal(text.match(/^MimeType=(.*)$/m)?.[1], schemes.length ? "x-scheme-handler/org.example.notes;x-scheme-handler/notes-x;" : undefined);
          assert.ok(text.endsWith("\n"));
        }
        if (file === "linux/app.metainfo.xml") {
          assert.match(text, /<developer id="org\.example">/);
          assert.match(text, /<url type="homepage">https:\/\/example\.org\/notes\?a=1&amp;b=2<\/url>/);
        }
        if (file === "linux/flatpak.yml") assert.match(text, /^app-id: org\.example\.Notes$/m);
      }
    }
});

test("the packaging probe declares what the template's packaging reads", () => {
  const probe = readFileSync(resolve(root, "tests/native/Runic.Packaging.Probe/Runic.Packaging.Probe.csproj"), "utf8");
  const template = readFileSync(resolve(packaging, "../RunicWindowApp.csproj"), "utf8");
  for (const property of ["RunicApplicationId", "RunicApplicationName", "Version", "Description", "PackageProjectUrl", "RunicApplicationPublisher", "RunicApplicationUrlSchemes"]) {
    assert.match(probe, new RegExp(`<${property}>[^<]+</${property}>`), property);
    assert.match(template, new RegExp(`<${property}>`), property);
  }
  assert.match(probe, /<Compile Include="\.\.\/\.\.\/\.\.\/tools\/Runic\.Application\.Templates\/content\/runic-app\/ApplicationIdentity\.cs"/);
});
