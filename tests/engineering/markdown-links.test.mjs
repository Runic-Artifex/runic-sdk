import { expect, test } from "bun:test";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { posix, resolve } from "node:path";
import { anchor } from "../../eng/release/notes.mjs";

const root = resolve(import.meta.dir, "../..");

// Blanks fenced and indented code blocks. Indented lines continue a list item
// rather than start code while a list is open.
function withoutCodeBlocks(markdown) {
  const kept = [];
  let fence = null, blank = true, code = false, list = false;
  for (const line of markdown.split("\n")) {
    const indent = line.match(/^[ \t]*/)[0].replace(/\t/g, "    ").length;
    const marker = line.trimStart().match(/^(`{3,}|~{3,})/)?.[1];
    if (fence) {
      if (marker?.[0] === fence[0] && marker.length >= fence.length && indent < 4) fence = null;
      kept.push("");
      continue;
    }
    if (marker && indent < 4) { fence = marker; kept.push(""); continue; }
    if (!line.trim()) { blank = true; kept.push(line); continue; }
    code = indent >= 4 && !list && (blank || code);
    if (!code && indent < 4) list = /^\s*([-*+]|\d+[.)])\s/.test(line) || (list && indent > 0);
    blank = false;
    kept.push(code ? "" : line);
  }
  return kept;
}

// The text outside code blocks and code spans. A code span closes at a
// backtick run of its opening run's length and stays inside its paragraph or
// table row, so an unmatched backtick does not pair with one further on.
const prose = markdown => withoutCodeBlocks(markdown).join("\n")
  .split(/(\n[ \t]*\n|\n(?=[ \t]*\|))/)
  .map(block => block.replace(/(?<!`)(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)/g, ""))
  .join("");

// Link targets outside code, as written.
function linkTargets(markdown) {
  const text = prose(markdown);
  const destination = String.raw`(?:<([^<>\n]*)>|((?:[^()\s]|\([^()\s]*\))+))`;
  return [
    ...[...text.matchAll(new RegExp(String.raw`!?\[(?:[^\][]|\[[^\]]*\])*\]\(\s*${destination}(?:\s+(?:"[^"]*"|'[^']*'|\([^()]*\)))?\s*\)`, "g"))]
      .map(match => match[1] ?? match[2]),
    ...[...text.matchAll(new RegExp(String.raw`^ {0,3}\[(?!\^)[^\]]+\]:[ \t]*${destination}`, "gm"))]
      .map(match => match[1] ?? match[2]),
    ...[...text.matchAll(/<(?:a|img)\s[^>]*?\b(?:href|src)="([^"]+)"/g)].map(match => match[1]),
  ].filter(Boolean);
}

const external = target => /^[a-z][a-z\d+.-]*:/i.test(target) || target.startsWith("//");

// Relative link targets in one Markdown document. External URLs and
// same-document anchors are out of scope.
export function relativeLinks(markdown) {
  return linkTargets(markdown)
    .filter(target => !external(target) && !target.startsWith("#"))
    .map(target => decodeURIComponent(target.replace(/[?#].*$/, "")))
    .filter(Boolean);
}

// The anchors GitHub renders for a document: each ATX heading's slug, repeats
// numbered as github-slugger does (`a`, `a-1`, `a-2`), and the `id` and `name`
// of HTML elements. GitHub lowercases both these ids and a link's fragment, so
// they are compared in lowercase.
export function anchors(markdown) {
  const found = new Set(), occurrences = new Map();
  for (const line of withoutCodeBlocks(markdown)) {
    const heading = line.match(/^ {0,3}#{1,6}(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/);
    if (!heading) continue;
    const base = anchor(heading[1] ?? "");
    let slug = base;
    while (found.has(slug)) {
      occurrences.set(base, (occurrences.get(base) ?? 0) + 1);
      slug = `${base}-${occurrences.get(base)}`;
    }
    found.add(slug);
  }
  for (const match of prose(markdown).matchAll(/<[a-z][a-z\d]*\s[^>]*?\b(?:id|name)="([^"]+)"/gi)) found.add(match[1].toLowerCase());
  return found;
}

// Fragment links (`#name`, `doc.md#name`) whose document renders no such
// anchor. Fragments of links to other kinds of files, such as line numbers,
// are out of scope; `documents` maps a tracked Markdown path to its text.
export function brokenAnchors(file, markdown, documents) {
  const cache = new Map();
  const anchorsOf = path => {
    if (!cache.has(path)) cache.set(path, anchors(documents(path)));
    return cache.get(path);
  };
  return linkTargets(markdown).filter(target => {
    const match = !external(target) && target.match(/^([^?#]*)#(.+)$/);
    if (!match) return false;
    const path = match[1]
      ? posix.normalize(match[1].startsWith("/") ? match[1].slice(1) : posix.join(posix.dirname(file), decodeURIComponent(match[1])))
      : file;
    if (!path.endsWith(".md") || documents(path) === undefined) return false;
    return !anchorsOf(path).has(decodeURIComponent(match[2]).toLowerCase());
  }).map(target => `${file}: ${target}`);
}

// Tracked files and every directory that contains one.
export function trackedPaths(files) {
  const paths = new Set();
  for (const file of files)
    for (let path = file; path && path !== "."; path = posix.dirname(path)) paths.add(path);
  return paths;
}

export function brokenLinks(file, markdown, tracked) {
  return relativeLinks(markdown).filter(target => {
    const path = posix.normalize(target.startsWith("/") ? target.slice(1) : posix.join(posix.dirname(file), target))
      .replace(/\/+$/, "");
    return !tracked.has(path) && path !== ".";
  }).map(target => `${file}: ${target}`);
}

test("the link checker reports missing relative targets only", () => {
  const tracked = trackedPaths(["README.md", "docs/guide (draft).md", "docs/a b.md", "src/index.ts"]);
  const markdown = [
    "[ok](README.md#readme) [dir](docs/) [root](/src/index.ts) [missing](docs/missing.md) ![image](./nope.png)",
    "[parens](docs/guide%20(draft).md) [spaces](<docs/a b.md> \"title\") [bad-spaces](<docs/c d.md>)",
    "[external](https://example.com/x.md) [anchor](#local) [mail](mailto:a@example.com)",
    "`[code](inline-missing.md)`",
    "```md\n[fenced](fenced-missing.md)\n```",
    "",
    "    [indented](indented-missing.md)",
    "",
    "- item",
    "",
    "    [continued](continued-missing.md)",
    "",
    "Text[^note].",
    "",
    "[^note]: footnote-not-a-link.md",
    "[ref]: ../outside-missing.md",
    '<a href="html-missing.md">html</a>',
  ].join("\n");
  expect(brokenLinks("README.md", markdown, tracked)).toEqual([
    "README.md: docs/missing.md",
    "README.md: ./nope.png",
    "README.md: docs/c d.md",
    "README.md: continued-missing.md",
    "README.md: ../outside-missing.md",
    "README.md: html-missing.md",
  ]);
});

test("the anchor checker matches GitHub's heading and HTML anchors", () => {
  const guide = [
    "# Guide",
    "## Setup and `retries` (0.10)",
    "## Notes",
    "## Notes",
    "### Notes 1 ##",
    '<a id="RUNICDESK105"></a><a name="legacy"></a>',
    "```md\n## Fenced heading\n```",
    "    ## Indented code",
  ].join("\n");
  const readme = [
    "[ok](#local) [missing](#absent) [upper](#LOCAL)",
    "[cross](docs/guide.md#setup-and-retries-010) [repeat](docs/guide.md#notes-1) [again](docs/guide.md#notes-1-1)",
    "[id](docs/guide.md#runicdesk105) [case](docs/guide.md#RUNICDESK105) [name](docs/guide.md#legacy)",
    "[fenced](docs/guide.md#fenced-heading) [indented](docs/guide.md#indented-code) [gone](./docs/guide.md#gone)",
    "[line](src/index.ts#L10) [untracked](docs/missing.md#x) [external](https://example.com/a.md#x)",
    "`[code](#in-code)` ``[`double`](#in-double)``",
    "An unmatched ` backtick.",
    "",
    "| `a | [cell](#after-unmatched) |",
    "| `` `b` `` | ``[`c`](#in-row)`` |",
    "",
    "## Local",
  ].join("\n");
  const documents = path => ({ "README.md": readme, "docs/guide.md": guide })[path];
  expect(brokenAnchors("README.md", readme, documents)).toEqual([
    "README.md: #absent",
    "README.md: docs/guide.md#fenced-heading",
    "README.md: docs/guide.md#indented-code",
    "README.md: ./docs/guide.md#gone",
    "README.md: #after-unmatched",
  ]);
});

const files = () => execFileSync("git", ["ls-files", "-z"], { cwd: root, encoding: "utf8" }).split("\0").filter(Boolean);

// Release-note fragments (eng/release/notes/<version>/) link as the assembled
// <version>.md does; eng/release/notes.test.mjs checks them there.
const checkedDocuments = tracked => tracked.filter(file => file.endsWith(".md") && !/^eng\/release\/notes\/[^/]+\//.test(file)
  && existsSync(resolve(root, file)));

test("tracked Markdown files have no broken relative links", () => {
  const tracked = files();
  const documents = checkedDocuments(tracked);
  expect(documents.length).toBeGreaterThan(0);
  expect(documents.flatMap(file => brokenLinks(file, readFileSync(resolve(root, file), "utf8"), trackedPaths(tracked)))).toEqual([]);
});

test("tracked Markdown files link only to anchors their target renders", () => {
  const tracked = new Set(files());
  const texts = new Map();
  const documents = path => {
    if (!tracked.has(path) || !existsSync(resolve(root, path))) return undefined;
    if (!texts.has(path)) texts.set(path, readFileSync(resolve(root, path), "utf8"));
    return texts.get(path);
  };
  expect(checkedDocuments([...tracked]).flatMap(file => brokenAnchors(file, documents(file), documents))).toEqual([]);
});
