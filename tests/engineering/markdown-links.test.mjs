import { expect, test } from "bun:test";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";

const root = resolve(import.meta.dir, "../..");

// Relative link targets in one Markdown document. External URLs and
// same-document anchors are out of scope; fenced and inline code is skipped.
export function relativeLinks(markdown) {
  const text = markdown
    .replace(/^ {0,3}(```|~~~)[^\n]*\n[\s\S]*?^ {0,3}\1[^\n]*$/gm, "")
    .replace(/(`+)[\s\S]*?\1/g, "");
  const targets = [
    ...[...text.matchAll(/!?\[(?:[^\][]|\[[^\]]*\])*\]\(\s*<?([^)\s>]+)>?(?:\s+(?:"[^"]*"|'[^']*'))?\s*\)/g)].map(match => match[1]),
    ...[...text.matchAll(/^ {0,3}\[[^\]]+\]:\s*<?([^\s>]+)>?/gm)].map(match => match[1]),
    ...[...text.matchAll(/<(?:a|img)\s[^>]*?\b(?:href|src)="([^"]+)"/g)].map(match => match[1]),
  ];
  return targets
    .filter(target => !/^[a-z][a-z\d+.-]*:/i.test(target) && !target.startsWith("#") && !target.startsWith("//"))
    .map(target => decodeURIComponent(target.replace(/[?#].*$/, "")))
    .filter(Boolean);
}

export function brokenLinks(file, markdown, base = root) {
  return relativeLinks(markdown)
    .filter(target => !existsSync(target.startsWith("/") ? resolve(base, `.${target}`) : resolve(base, dirname(file), target)))
    .map(target => `${file}: ${target}`);
}

test("the link checker reports missing relative targets only", () => {
  const markdown = [
    "[ok](README.md#readme) [missing](docs/missing.md) ![image](./nope.png)",
    "[external](https://example.com/x.md) [anchor](#local) [mail](mailto:a@example.com)",
    "`[code](inline-missing.md)`",
    "```md\n[fenced](fenced-missing.md)\n```",
    "[ref]: ../outside-missing.md",
    '<a href="html-missing.md">html</a>',
  ].join("\n");
  expect(brokenLinks("README.md", markdown)).toEqual([
    "README.md: docs/missing.md",
    "README.md: ./nope.png",
    "README.md: ../outside-missing.md",
    "README.md: html-missing.md",
  ]);
});

test("tracked Markdown files have no broken relative links", () => {
  const files = execFileSync("git", ["ls-files", "-z", "--", "*.md"], { cwd: root, encoding: "utf8" })
    .split("\0").filter(file => file && existsSync(resolve(root, file)));
  expect(files.length).toBeGreaterThan(0);
  const broken = files.flatMap(file => brokenLinks(file, readFileSync(resolve(root, file), "utf8")));
  expect(broken).toEqual([]);
});
