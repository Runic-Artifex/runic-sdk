// Runs the managed test suites of one or more groups (eng/ci/plan.mjs, managedGroups) from the build's outputs.
//
//   bun eng/ci/test-managed.mjs [--parallel <n>] <group>...
//
// C4 (D077.25): with --parallel, up to <n> test projects run at once (each its own process; TUnit's own parallelism and a
// project's [NotInParallel] constraints apply inside it). The longest projects start first (`slowFirst`), so the job takes
// about as long as the longest one. Each project's output is printed in one piece when it ends, in a log group on
// GitHub Actions, and every project runs even when another failed; the step fails at the end with the list. The default
// of 1 runs them one after another, as before.
import { spawn } from "node:child_process";
import { appendFileSync } from "node:fs";
import { basename, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { root, configuration } from "../run.mjs";
import { managedGroups, managedTests, applicationFixtureCommands, testReportArguments } from "./plan.mjs";

// The projects that take longest, measured in CI (queue run 44847659246, 2026-11-21): both are [assembly: NotInParallel],
// so they run their tests one at a time. They start first; the rest keep the solution's order.
export const slowFirst = ["Runic.Application.Testing.Tests", "Runic.Application.Views.Desktop.Tests", "Runic.Application.Tool.Tests"];

export function parseArguments(argv) {
  const options = { parallel: 1, coverage: true, groups: [] };
  for (let index = 0; index < argv.length; index++) {
    if (argv[index] === "--parallel") options.parallel = Number(argv[++index]);
    else if (argv[index] === "--no-coverage") options.coverage = false;
    else options.groups.push(argv[index]);
  }
  if (!Number.isInteger(options.parallel) || options.parallel < 1) throw new Error("--parallel <n> must be a positive integer");
  if (!options.groups.length) throw new Error(`Choose managed suites: ${managedGroups.join(", ")}`);
  for (const group of options.groups)
    if (!managedGroups.includes(group)) throw new Error(`Choose managed suites: ${managedGroups.join(", ")}, not '${group}'`);
  if (new Set(options.groups).size !== options.groups.length) throw new Error("A group is named twice");
  return options;
}

// Every command of the groups as tasks: one per test project, and the application fixtures as one task (they build, one
// after another, after the build job built only RunicSdk.Core.slnx). The slow projects first. `reportArguments` is
// plan.mjs' testReportArguments, which also clears the project's earlier results.
export function tasks(groups, { base = root, platform = process.platform, coverage = true, reportArguments = testReportArguments } = {}) {
  const list = [];
  for (const group of groups) {
    const projects = managedTests(base, platform).filter(item => item.group === group);
    if (!projects.length) throw new Error(`No tests in ${group}`);
    for (const { path } of projects) {
      // Each group's reports go to their own directory, which the managed job uploads per group.
      const all = reportArguments(path, [], base, `artifacts/test-results/${group}`);
      // --no-coverage keeps the TRX report and drops the Cobertura coverage (its arguments start at --coverage).
      const reports = coverage || !all.includes("--coverage") ? all : all.slice(0, all.indexOf("--coverage"));
      list.push({ name: basename(path, ".csproj"), group,
        commands: [["dotnet", ["run", "--project", resolve(base, path), "-c", configuration, "--no-build", ...(reports.length ? ["--", ...reports] : [])]]] });
    }
    if (group === "application") {
      const commands = applicationFixtureCommands(configuration, base, platform);
      if (commands.length) list.push({ name: "application fixtures", group, commands });
    }
  }
  const rank = task => { const at = slowFirst.indexOf(task.name); return at < 0 ? slowFirst.length : at; };
  return list.map((task, at) => ({ task, at })).sort((a, b) => rank(a.task) - rank(b.task) || a.at - b.at).map(({ task }) => task);
}

const quote = arg => /[\s"]/.test(arg) ? JSON.stringify(arg) : arg;

// Runs a task's commands one after another, collecting their output; stops at the first that fails.
function runTask(task, live) {
  return new Promise(done => {
    const started = Date.now();
    let output = "";
    const write = text => { if (live) process.stdout.write(text); else output += text; };
    const next = index => {
      if (index === task.commands.length) return done({ task, code: 0, output, seconds: (Date.now() - started) / 1000 });
      const [command, args] = task.commands[index];
      write(`\n> ${[command, ...args].map(quote).join(" ")}\n`);
      const child = spawn(command, args, { cwd: root, stdio: ["ignore", "pipe", "pipe"], env: process.env });
      child.stdout.on("data", chunk => write(chunk.toString()));
      child.stderr.on("data", chunk => write(chunk.toString()));
      child.on("error", error => write(`\n${error.stack}\n`));
      child.on("close", (code, signal) => {
        const status = code ?? (signal ? 1 : 0);
        if (status !== 0) return done({ task, code: status, output, seconds: (Date.now() - started) / 1000 });
        next(index + 1);
      });
    };
    next(0);
  });
}

// Runs `list` with at most `parallel` tasks at once, in order; resolves with every result in the order they ended.
export async function runAll(list, parallel, run = runTask, report = () => {}) {
  const results = [];
  let index = 0;
  const worker = async () => {
    while (index < list.length) {
      const result = await run(list[index++], parallel === 1);
      results.push(result);
      report(result);
    }
  };
  await Promise.all(Array.from({ length: Math.min(parallel, list.length) }, worker));
  return results;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { parallel, coverage, groups } = parseArguments(process.argv.slice(2));
  const list = tasks(groups, { coverage });
  const actions = process.env.GITHUB_ACTIONS === "true";
  const started = Date.now();
  console.log(`Running ${list.length} managed test tasks of ${groups.join(", ")}, ${parallel} at once${coverage ? "" : ", without coverage"}.`);
  const results = await runAll(list, parallel, runTask, ({ task, code, output, seconds }) => {
    const title = `${task.name} (${task.group}): ${code === 0 ? "passed" : `failed with exit code ${code}`} in ${seconds.toFixed(0)} s`;
    if (parallel === 1) return console.log(`\n${title}`);
    // A failed task's output stays outside a log group, so it shows without expanding anything.
    if (actions && code === 0) console.log(`::group::${title}\n${output}\n::endgroup::`);
    else console.log(`\n===== ${title}\n${output}`);
    if (code !== 0) console.log(`::error title=${task.name} failed::${title}`);
  });
  const failed = results.filter(result => result.code !== 0);
  const wall = ((Date.now() - started) / 1000).toFixed(0);
  const longest = results.toSorted((a, b) => b.seconds - a.seconds);
  const rows = longest.map(({ task, code, seconds }) => `| ${task.name} | ${task.group} | ${seconds.toFixed(0)} | ${code === 0 ? "passed" : `failed (${code})`} |`);
  console.log(`\n${results.length} tasks in ${wall} s, ${parallel} at once; the longest: `
    + longest.slice(0, 3).map(({ task, seconds }) => `${task.name} ${seconds.toFixed(0)} s`).join(", "));
  if (process.env.GITHUB_STEP_SUMMARY)
    appendFileSync(process.env.GITHUB_STEP_SUMMARY, `\n### Managed suites: ${groups.join(", ")} (${wall} s, ${parallel} at once)\n\n`
      + `| Project | Group | Seconds | Result |\n|---|---|---|---|\n${rows.join("\n")}\n`);
  if (failed.length) {
    console.log(`Failed: ${failed.map(({ task }) => task.name).join(", ")}`);
    process.exit(1);
  }
}
