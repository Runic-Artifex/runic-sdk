// Startup and memory of a published Runic Desktop application in serve-only mode (X1; Linux, macOS and Windows since
// X2): the time from process start until the surface printed RUNIC_APPLICATION_URL, until its page, bootstrap script
// and Views client were served, and the resident memory then and at its peak (Linux: VmRSS and VmHWM; macOS: ps's
// RSS, no peak; Windows: the working set and its peak).
//
//   node tests/benchmarks/native-server/startup.mjs <executable> [runs]
//
// Prints one JSON object with the medians. The application exits on a line on standard input, as serve-only does.
import { execFileSync, spawn } from "node:child_process";
import { readFileSync } from "node:fs";

const [executable, runsArgument = "15"] = process.argv.slice(2);
if (!executable) {
  console.error("usage: node startup.mjs <executable> [runs]");
  process.exit(2);
}
const runs = Number(runsArgument);
const kib = (status, field) => Number(status.match(new RegExp(`^${field}:\\s+(\\d+) kB`, "m"))?.[1] ?? NaN);
const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];

// Resident memory in KiB, now and at its peak where the platform reports it.
function memory(pid) {
  if (process.platform === "linux") {
    const status = readFileSync(`/proc/${pid}/status`, "utf8");
    return { rssKiB: kib(status, "VmRSS"), peakKiB: kib(status, "VmHWM") };
  }
  if (process.platform === "darwin") {
    const rss = Number(execFileSync("ps", ["-o", "rss=", "-p", String(pid)], { encoding: "utf8" }).trim());
    return { rssKiB: rss, peakKiB: NaN };
  }
  const [workingSet, peak] = execFileSync("powershell", ["-NoProfile", "-Command",
    `$p = Get-Process -Id ${pid}; "$($p.WorkingSet64) $($p.PeakWorkingSet64)"`], { encoding: "utf8" }).trim().split(/\s+/).map(Number);
  return { rssKiB: workingSet / 1024, peakKiB: peak / 1024 };
}

async function once() {
  const started = process.hrtime.bigint();
  const child = spawn(executable, [], {
    stdio: ["pipe", "pipe", "pipe"],
    env: { ...process.env, RUNIC_APPLICATION_SERVE_ONLY: "1" },
  });
  let output = "";
  let errors = "";
  child.stderr.on("data", chunk => { errors += chunk; });
  const url = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`no URL within 30 s: ${errors}`)), 30_000);
    child.on("exit", code => reject(new Error(`exited with ${code}: ${errors}`)));
    child.stdout.on("data", chunk => {
      output += chunk;
      const match = output.match(/RUNIC_APPLICATION_URL=(\S+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
  });
  const listening = Number(process.hrtime.bigint() - started) / 1e6;
  // What a window loads before the page can connect: the entry (after the index redirect) and its scripts.
  const page = await fetch(url, { redirect: "follow" });
  if (!page.ok) throw new Error(`the page answered ${page.status}`);
  await page.text();
  for (const script of ["webui.js", "runic-views.js"]) {
    const response = await fetch(new URL(script, page.url));
    if (!response.ok) throw new Error(`${script} answered ${response.status}`);
    await response.arrayBuffer();
  }
  const served = Number(process.hrtime.bigint() - started) / 1e6;
  const result = { listening, served, ...memory(child.pid) };
  const exited = new Promise(resolve => child.on("exit", resolve));
  child.stdin.end("\n");
  const timer = setTimeout(() => child.kill("SIGKILL"), 10_000);
  await exited;
  clearTimeout(timer);
  return result;
}

await once(); // Warms the file cache; not counted.
const results = [];
for (let index = 0; index < runs; index++) results.push(await once());
console.log(JSON.stringify({
  executable,
  runs,
  listeningMs: Math.round(median(results.map(result => result.listening)) * 10) / 10,
  servedMs: Math.round(median(results.map(result => result.served)) * 10) / 10,
  rssMiB: Math.round(median(results.map(result => result.rssKiB)) / 102.4) / 10,
  peakMiB: Number.isNaN(results[0].peakKiB) ? null : Math.round(median(results.map(result => result.peakKiB)) / 102.4) / 10,
}));
