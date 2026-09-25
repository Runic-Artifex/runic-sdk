import { existsSync, readFileSync, readdirSync, rmSync, unlinkSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const project = dirname(fileURLToPath(import.meta.url));
const sessionPath = join(project, "obj", "runic-ide-session.json");
if (!existsSync(sessionPath)) process.exit(0);

const { pid, url, profilePath } = JSON.parse(readFileSync(sessionPath, "utf8"));
const profileRoot = resolve(project, "obj", "runic-ide-browser");
if (!Number.isSafeInteger(pid) || pid < 1 || !/^http:\/\/127\.0\.0\.1:[1-9][0-9]*\/$/.test(url) ||
    typeof profilePath !== "string" || dirname(resolve(profilePath)) !== profileRoot ||
    !new RegExp(`^${pid}-[0-9a-f]{32}$`).test(basename(profilePath)))
  throw new Error("Invalid Reactive Notes IDE session record.");

if (process.platform === "linux") {
  const appArgument = `--app=${url}`;
  const profileArgument = `--user-data-dir=${profilePath}`;
  for (const entry of readdirSync("/proc")) {
    if (!/^[1-9][0-9]*$/.test(entry)) continue;
    try {
      const args = readFileSync(`/proc/${entry}/cmdline`, "utf8").split("\0");
      if (args.some(arg => arg.includes(appArgument)) && args.some(arg => arg.includes(profileArgument)))
        process.kill(Number(entry), "SIGKILL");
      else if (args.some(arg => arg.endsWith("notes-ide-frontend.mjs")) && args.includes(String(pid)))
        process.kill(Number(entry), "SIGTERM");
    } catch { /* Process already closed or belongs to another user. */ }
  }
}
unlinkSync(sessionPath);
rmSync(profilePath, { recursive: true, force: true });
