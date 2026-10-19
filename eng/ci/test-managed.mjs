import { resolve } from "node:path";
import { root, run, configuration } from "../run.mjs";
import { managedGroups, managedTests, applicationFixtureCommands, testReportArguments } from "./plan.mjs";

const group = process.argv[2];
if (!managedGroups.includes(group)) throw new Error(`Choose a managed suite: ${managedGroups.join(", ")}`);
const projects = managedTests().filter(item => item.group === group);
if (!projects.length) throw new Error(`No tests in ${group}`);
for (const { path } of projects) {
  // Each group's reports go to their own directory, which the managed job uploads per group.
  const reports = testReportArguments(path, [], root, `artifacts/test-results/${group}`);
  run("dotnet", ["run", "--project", resolve(root, path), "-c", configuration, "--no-build", ...(reports.length ? ["--", ...reports] : [])]);
}
// The build job builds only RunicSdk.Core.slnx, so the fixtures build here.
if (group === "application")
  for (const [command, args] of applicationFixtureCommands(configuration)) run(command, args);
