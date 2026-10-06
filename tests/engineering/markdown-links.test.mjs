import { expect, test } from "bun:test";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { posix, resolve } from "node:path";

const root = resolve(import.meta.dir, "../..");

// Removes fenced and indented code blocks and inline code. Indented lines
// continue a list item rather than start code while a list is open.
function prose(markdown) {
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
  return kept.join("\n").replace(/(`+)[\s\S]*?\1/g, "");
}

// Relative link targets in one Markdown document. External URLs and
// same-document anchors are out of scope.
export function relativeLinks(markdown) {
  const text = prose(markdown);
  const destination = String.raw`(?:<([^<>\n]*)>|((?:[^()\s]|\([^()\s]*\))+))`;
  const targets = [
    ...[...text.matchAll(new RegExp(String.raw`!?\[(?:[^\][]|\[[^\]]*\])*\]\(\s*${destination}(?:\s+(?:"[^"]*"|'[^']*'|\([^()]*\)))?\s*\)`, "g"))]
      .map(match => match[1] ?? match[2]),
    ...[...text.matchAll(new RegExp(String.raw`^ {0,3}\[(?!\^)[^\]]+\]:[ \t]*${destination}`, "gm"))]
      .map(match => match[1] ?? match[2]),
    ...[...text.matchAll(/<(?:a|img)\s[^>]*?\b(?:href|src)="([^"]+)"/g)].map(match => match[1]),
  ];
  return targets
    .filter(target => target && !/^[a-z][a-z\d+.-]*:/i.test(target) && !target.startsWith("#") && !target.startsWith("//"))
    .map(target => decodeURIComponent(target.replace(/[?#].*$/, "")))
    .filter(Boolean);
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

test("tracked Markdown files have no broken relative links", () => {
  const files = execFileSync("git", ["ls-files", "-z"], { cwd: root, encoding: "utf8" }).split("\0").filter(Boolean);
  const tracked = trackedPaths(files);
  const documents = files.filter(file => file.endsWith(".md") && existsSync(resolve(root, file)));
  expect(documents.length).toBeGreaterThan(0);
  expect(documents.flatMap(file => brokenLinks(file, readFileSync(resolve(root, file), "utf8"), tracked))).toEqual([]);
});
