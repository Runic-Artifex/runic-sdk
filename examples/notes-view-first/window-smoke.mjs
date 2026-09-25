import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const dll = fileURLToPath(new URL("./bin/Release/net10.0/NotesViewFirst.dll", import.meta.url));
const host = spawn("dotnet", [dll, "--two-windows", "--serve-only", ...(process.env.RUNIC_VIEW_LOCATOR === "splat" ? ["--splat"] : [])], {
  stdio: ["pipe", "pipe", "pipe"]
});
let output = "", errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
try {
  const deadline = Date.now() + 10_000;
  let urls;
  while (Date.now() < deadline) {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    urls = [...output.matchAll(/https?:\/\/[^\s]+/g)].map(match => match[0]);
    if (urls.length >= 2) break;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  if (!urls || urls.length !== 2 || urls[0] === urls[1])
    throw new Error(`Expected two distinct window server URLs: ${output} ${errors}`);
  for (const url of urls) {
    const response = await fetch(url);
    const html = await response.text();
    if (!response.ok || !html.includes("Composed Notes"))
      throw new Error(`Window did not serve the app: ${url} ${response.status}`);
  }
  console.log("NOTES_WINDOWS_OK|distinct-scopes|two-servers");
} finally {
  host.stdin.end("\n");
  await new Promise(resolve => setTimeout(resolve, 250));
  if (host.exitCode === null) host.kill("SIGTERM");
}
