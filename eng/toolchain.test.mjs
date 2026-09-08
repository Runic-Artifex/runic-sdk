import assert from "node:assert/strict";
import { test } from "node:test";
import { parseToolchain, readToolchain } from "./toolchain.mjs";
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
