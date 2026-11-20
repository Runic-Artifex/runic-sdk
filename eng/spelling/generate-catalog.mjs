#!/usr/bin/env bun
// Generates Runic.Spelling's catalogue of downloadable Hunspell dictionaries,
// packages/dotnet/Runic.Spelling/catalogue/catalogue.json:
//
//   bun eng/spelling/generate-catalog.mjs
//
// The dictionaries come from two repositories, each at the fixed commit below (never a branch):
//
// - wooorm/dictionaries for every language but German: one folder per BCP 47 tag with index.dic and index.aff, all in
//   UTF-8, a readme naming the source and licence, a licence file (all but et, whose readme names its licence alone;
//   oc spells it "licence") and the licence's SPDX expression in package.json.
// - LibreOffice/dictionaries for German (de-DE, de-AT, de-CH): wooorm's de-CH is not Swiss spelling (it has Straße and
//   lacks Strasse), where LibreOffice's frami dictionaries, built from the same igerman98, are. Their files, readme and
//   licence are listed here by hand, as LibreOffice's folders say which licence applies only in prose.
//
// Every entry names its files by raw.githubusercontent.com URLs at its commit, with each file's SHA-256 and size and
// the name it is installed under. Each file is checked against the Git blob ID in the commit's tree, so the hashes are
// those of the bytes the URLs serve. Commit the result. Requests carry a generic User-Agent and nothing else.
//
// Dictionaries whose tag has a variant (ca-valencia, el-polyton) are left out: a Hunspell name has no place for a
// variant, so they would read as the language's other dictionary (packages/dotnet/Runic.Spelling/README.md).
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../..", import.meta.url));
const output = resolve(root, "packages/dotnet/Runic.Spelling/catalogue/catalogue.json");
const headers = { "User-Agent": "runic-sdk-spelling-catalogue", Accept: "application/vnd.github+json" };

// 2024-09-09, "Regenerate Ukrainian": the newest commit of wooorm/dictionaries when the catalogue was added.
const wooorm = { repository: "wooorm/dictionaries", commit: "8cfea406b505e4d7df52d5a19bce525df98c54ab" };
// 2026-10-12, "Add Ligurian (lij_IT) spelling and hyphenation dictionaries": a LibreOffice/dictionaries commit some
// weeks old when the catalogue was added; its German files date from 2017 (frami 20170109).
const libreOffice = { repository: "LibreOffice/dictionaries", commit: "67032ee957631908098a72edf48dc81cac27e369" };

// wooorm's files: the dictionary, its affixes, its readme and its licence text.
const wooormFiles = [["dic", ["index.dic"]], ["aff", ["index.aff"]], ["readme", ["readme.md"], "README.md"], ["license", ["license", "licence"], "LICENSE", true]];
// German from LibreOffice in place of wooorm's de, de-AT and de-CH. README_de_DE_frami.txt: "Das Wörterbuch und alle
// enthaltenen Wortlisten sind lizenziert unter der GNU GPL, Version 2 oder 3."
const german = ["DE", "AT", "CH"].map(region => ({
  tag: `de-${region}`,
  license: "(GPL-2.0 OR GPL-3.0)",
  files: [
    ["dic", `de/de_${region}_frami.dic`],
    ["aff", `de/de_${region}_frami.aff`],
    ["readme", "de/README_de_DE_frami.txt", "README_de_DE_frami.txt"],
    ["license", "de/COPYING_GPLv2", "COPYING_GPLv2"],
    ["license", "de/COPYING_GPLv3", "COPYING_GPLv3"],
  ],
}));
const replacedByLibreOffice = new Set(["de", "de-AT", "de-CH"]);

// A BCP 47 tag as a Hunspell dictionary name (de-CH -> de_CH, sr-Latn -> sr_Latn); null for a tag with a variant.
export function dictionaryName(tag) {
  const [language, ...rest] = tag.split("-");
  if (!/^[a-z]{2,3}$/i.test(language)) return null;
  const parts = { language: language.toLowerCase(), script: null, region: null };
  if (rest.length && /^[a-z]{4}$/i.test(rest[0])) {
    const script = rest.shift();
    parts.script = script[0].toUpperCase() + script.slice(1).toLowerCase();
  }
  if (rest.length && /^([a-z]{2}|[0-9]{3})$/i.test(rest[0])) parts.region = rest.shift().toUpperCase();
  if (rest.length) return null;
  return { ...parts, name: [parts.language, parts.script, parts.region].filter(Boolean).join("_") };
}

async function json(url) {
  const response = await fetch(url, { headers });
  assert.ok(response.ok, `${url}: ${response.status}`);
  return response.json();
}

async function bytesOf(url) {
  const response = await fetch(url, { headers });
  assert.ok(response.ok, `${url}: ${response.status}`);
  return Buffer.from(await response.arrayBuffer());
}

const gitBlobId = bytes => createHash("sha1").update(`blob ${bytes.length}\0`).update(bytes).digest("hex");

// The commit's blobs by path, to check the bytes against.
async function blobsOf({ repository, commit }) {
  const tree = await json(`https://api.github.com/repos/${repository}/git/trees/${commit}?recursive=1`);
  assert.equal(tree.truncated, false, `The tree of ${repository} was truncated`);
  return new Map(tree.tree.filter(entry => entry.type === "blob").map(entry => [entry.path, entry.sha]));
}

function file(source, blobs, kind, path, bytes, installedAs) {
  assert.equal(gitBlobId(bytes), blobs.get(path), `${source.repository}: ${path} differs from the commit's tree`);
  return {
    kind,
    url: `https://raw.githubusercontent.com/${source.repository}/${source.commit}/${path}`,
    ...(installedAs ? { file: installedAs } : {}),
    sha256: createHash("sha256").update(bytes).digest("hex"),
    size: bytes.length,
  };
}

function entry(source, tag, license, files) {
  const named = dictionaryName(tag);
  return {
    tag,
    name: named.name,
    language: named.language,
    ...(named.script ? { script: named.script } : {}),
    ...(named.region ? { region: named.region } : {}),
    license,
    repository: `https://github.com/${source.repository}`,
    commit: source.commit,
    size: files.reduce((sum, item) => sum + item.size, 0),
    files,
  };
}

// wooorm/dictionaries, from the repository's archive at the commit, read once.
async function fromWooorm(work, skipped) {
  const blobs = await blobsOf(wooorm);
  const archive = join(work, "wooorm.tar.gz");
  writeFileSync(archive, await bytesOf(`https://codeload.github.com/${wooorm.repository}/tar.gz/${wooorm.commit}`));
  const extracted = join(work, "wooorm");
  mkdirSync(extracted);
  execFileSync("tar", ["-xzf", archive, "-C", extracted, "--strip-components=1", "--wildcards", "*/dictionaries/*"]);
  const entries = [];
  for (const tag of readdirSync(join(extracted, "dictionaries")).sort()) {
    if (replacedByLibreOffice.has(tag)) continue;
    if (!dictionaryName(tag)) { skipped.push(tag); continue; }
    const manifest = JSON.parse(readFileSync(join(extracted, "dictionaries", tag, "package.json"), "utf8"));
    assert.equal(typeof manifest.license, "string", `${tag}: package.json has no licence`);
    const files = wooormFiles.flatMap(([kind, names, installedAs, optional]) => {
      const name = names.find(candidate => blobs.has(`dictionaries/${tag}/${candidate}`));
      if (!name) {
        assert.ok(optional, `${tag} has no ${names.join(" or ")}`);
        return [];
      }
      const path = `dictionaries/${tag}/${name}`;
      return [file(wooorm, blobs, kind, path, readFileSync(join(extracted, path)), installedAs)];
    });
    entries.push(entry(wooorm, tag, manifest.license, files));
  }
  return entries;
}

// LibreOffice/dictionaries' German, file by file.
async function fromLibreOffice() {
  const blobs = await blobsOf(libreOffice);
  const entries = [];
  for (const dictionary of german) {
    const files = [];
    for (const [kind, path, installedAs] of dictionary.files)
      files.push(file(libreOffice, blobs, kind, path,
        await bytesOf(`https://raw.githubusercontent.com/${libreOffice.repository}/${libreOffice.commit}/${path}`), installedAs));
    entries.push(entry(libreOffice, dictionary.tag, dictionary.license, files));
  }
  return entries;
}

async function main() {
  const work = mkdtempSync(join(tmpdir(), "runic-spelling-catalogue-"));
  try {
    const skipped = [];
    const dictionaries = [...await fromWooorm(work, skipped), ...await fromLibreOffice()]
      .sort((a, b) => (a.tag < b.tag ? -1 : a.tag > b.tag ? 1 : 0));
    const catalogue = {
      schema: "runic.spelling.catalogue/1",
      sources: [wooorm, libreOffice].map(source => ({ repository: `https://github.com/${source.repository}`, commit: source.commit })),
      skipped,
      dictionaries,
    };
    writeFileSync(output, `${JSON.stringify(catalogue, null, 2)}\n`);
    console.log(`${dictionaries.length} dictionaries; left out: ${skipped.join(", ") || "none"}.`);
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
