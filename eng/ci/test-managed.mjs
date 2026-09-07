import { resolve } from "node:path";
import { root, run, configuration } from "../run.mjs";
import { managedGroups, managedTests } from "./plan.mjs";

const group = process.argv[2];
if (!managedGroups.includes(group)) throw new Error(`Choose a managed suite: ${managedGroups.join(", ")}`);
const projects = managedTests().filter(item => item.group === group);
if (!projects.length) throw new Error(`No tests in ${group}`);
for (const { path } of projects)
  run("dotnet", ["run", "--project", resolve(root, path), "-c", configuration, "--no-build"]);
