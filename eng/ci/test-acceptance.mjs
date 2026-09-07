import { readdirSync } from "node:fs";
import { resolve } from "node:path";
import { root, run } from "../run.mjs";

const directory = "tests/engineering/acceptance";
const tests = readdirSync(resolve(root, directory), { withFileTypes: true })
  .filter(entry => entry.isDirectory() && entry.name.startsWith("current-"))
  .flatMap(entry => readdirSync(resolve(root, directory, entry.name))
    .filter(name => name.endsWith(".test.mjs"))
    .map(name => `./${directory}/${entry.name}/${name}`)).sort();
if (!tests.length) throw new Error("No current acceptance tests found");
run("bun", ["test", "--timeout", "180000", ...tests]);
