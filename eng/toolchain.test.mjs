import assert from "node:assert/strict";
import { test } from "node:test";
import { checkToolchainCopies, parseToolchain, readToolchain } from "./toolchain.mjs";
const inputs = () => ({global:{sdk:{version:"10.0.400"}},node:"24.20.0\n",workspace:{packageManager:"bun@1.4.2"},setup:"bun-version: 1.4.2\nrun: npm install --global npm@12.0.2 pnpm@12.3.4 --allow-scripts=pnpm\n"});
test("toolchain reads maintained pins without a historical authority", () => {
  assert.deepEqual(parseToolchain(inputs()),{dotnetSdk:"10.0.400",node:"24.20.0",bun:"1.4.2",npm:"12.0.2",pnpm:"12.3.4"});
  assert.equal(Object.keys(readToolchain()).length,5);
});
test("missing or ambiguous pins and differing Bun setup fail", () => {
  for(const edit of [x=>x.setup=x.setup.replace("npm@12.0.2", "npm@latest"), x=>x.setup+="npm@12.0.3\n", x=>x.setup=x.setup.replace("bun-version: 1.4.2", "bun-version: 1.4.1"),x=>x.node="24", x=>delete x.global.sdk, x=>x.workspace.packageManager="pnpm@12.3.4"]) {
    const value=inputs();edit(value);assert.throws(()=>parseToolchain(value));
  }
});
const copies = () => ({
  workspace: { engines: { bun: "1.4.2" } },
  templates: "<BunTemplateVersion Condition=\"x\">1.4.2</BunTemplateVersion>\n<NpmTemplateVersion Condition=\"x\">12.0.2</NpmTemplateVersion>\n<PnpmTemplateVersion Condition=\"x\">12.3.4</PnpmTemplateVersion>\n",
  flake: [
    'url = "https://github.com/oven-sh/bun/releases/download/bun-v1.4.2/bun-${bunArchive.platform}.zip";',
    'pname = "bun";\n  version = "1.4.2";',
    'url = "https://registry.npmjs.org/npm/-/npm-12.0.2.tgz";',
    'npmForCompatibility = pkgs.runCommand "npm-12.0.2" {',
    'pname = "pnpm";\n  version = "12.3.4";',
    'url = "https://registry.npmjs.org/@pnpm/exe.${pnpmArchive.platform}/-/exe.${pnpmArchive.platform}-12.3.4.tgz";',
  ].join("\n"),
});
test("template defaults, the workspace Bun engine and the Nix shell follow the pins", () => {
  const pins = parseToolchain(inputs());
  checkToolchainCopies(pins, copies());
  const edits = [
    x => x.workspace.engines.bun = "1.4.1",
    x => x.templates = x.templates.replace(">12.0.2<", ">12.0.3<"),
    x => x.templates = x.templates.replace(/<PnpmTemplateVersion.*\n/, ""),
    x => x.flake = x.flake.replace("bun-v1.4.2", "bun-v1.4.3"),
    x => x.flake = x.flake.replace('version = "1.4.2"', 'version = "1.4.3"'),
    x => x.flake = x.flake.replace("npm-12.0.2.tgz", "npm-12.0.3.tgz"),
    x => x.flake = x.flake.replace('"npm-12.0.2"', '"npm-12.0.3"'),
    x => x.flake = x.flake.replace('version = "12.3.4"', 'version = "12.3.5"'),
    x => x.flake = x.flake.replace("-12.3.4.tgz", "-12.3.5.tgz"),
  ];
  for (const edit of edits) {
    const value = copies(); edit(value);
    assert.throws(() => checkToolchainCopies(pins, value));
  }
});
