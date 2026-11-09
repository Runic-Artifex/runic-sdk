// Runs one native test command on a Windows runner and reports how it ended. Git Bash reports every Windows
// exception exit (an NTSTATUS such as 0xC0000409) other than a few signals as exit code 127, the code for a missing
// command, so a test host that crashed looked like one that never started. This prints the command, its process id
// and its real exit code, and after a crash the runner's Windows Error Reporting and event-log entries for it
// (eng/ci/windows-crash-report.ps1), with a minidump in artifacts/native-crash-dumps for the job's artifact.
// Usage: node eng/ci/windows-native-run.mjs <command> [arguments...]
import { spawnSync } from "node:child_process";
import { mkdirSync } from "node:fs";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// Exception codes a Windows process can end with, and what Git Bash's MSYS runtime reports for them.
const statuses = {
  0xc0000005: ["STATUS_ACCESS_VIOLATION", 139],
  0xc00000fd: ["STATUS_STACK_OVERFLOW", 139],
  0xc000001d: ["STATUS_ILLEGAL_INSTRUCTION", 132],
  0xc000013a: ["STATUS_CONTROL_C_EXIT", 130],
  0xc0000135: ["STATUS_DLL_NOT_FOUND", 127],
  0xc0000142: ["STATUS_DLL_INIT_FAILED", 127],
  0xc0000374: ["STATUS_HEAP_CORRUPTION", 127],
  0xc0000409: ["STATUS_STACK_BUFFER_OVERRUN (a fail-fast: __fastfail, a corrupted list entry or a security check)", 127],
  0xc000041d: ["STATUS_FATAL_USER_CALLBACK_EXCEPTION (an exception in a window procedure or hook)", 127],
  0xc0000602: ["STATUS_FAIL_FAST_EXCEPTION", 127],
  0xc000027b: ["STATUS_STOWED_EXCEPTION (a COM or WinRT fail-fast)", 127],
  0xe0434352: ["an unhandled .NET exception (0xE0434352)", 127],
  0x80131506: ["COR_E_EXECUTIONENGINE (.NET runtime fail-fast)", 6],
};

/** The exit code as Windows reports it, in hex when it is an NTSTATUS, and what Git Bash would have reported. */
export function describeExitCode(code) {
  const unsigned = code >>> 0;
  if (unsigned <= 255) return { crashed: false, text: `exit code ${unsigned}` };
  const hex = `0x${unsigned.toString(16).toUpperCase().padStart(8, "0")}`;
  const [name, bash] = statuses[unsigned] ?? ["an unknown exception or status code", unsigned >= 0xc0000000 ? 127 : unsigned & 0xff];
  return { crashed: unsigned >= 0x80000000, text: `exit code ${hex} (${unsigned}): ${name}; Git Bash reports ${bash}` };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [command, ...args] = process.argv.slice(2);
  if (!command) throw new Error("Usage: node eng/ci/windows-native-run.mjs <command> [arguments...]");
  const executable = /[\\/]/.test(command) ? resolve(command) : command;
  const image = basename(executable).toLowerCase().endsWith(".exe") ? basename(executable) : `${basename(executable)}.exe`;
  const dumps = resolve("artifacts/native-crash-dumps");
  const windows = process.platform === "win32";
  if (windows) {
    mkdirSync(dumps, { recursive: true });
    // WER writes a minidump of a crashed process with this image name (LocalDumps); the runner is a throwaway VM.
    const key = `HKLM\\SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting\\LocalDumps\\${image}`;
    for (const value of [["DumpFolder", "REG_EXPAND_SZ", dumps], ["DumpType", "REG_DWORD", "1"], ["DumpCount", "REG_DWORD", "5"]])
      spawnSync("reg", ["add", key, "/v", value[0], "/t", value[1], "/d", value[2], "/f"], { stdio: "ignore" });
  }
  const started = new Date();
  console.log(`> ${[executable, ...args].map(arg => /[\s"]/.test(arg) ? JSON.stringify(arg) : arg).join(" ")}`);
  const child = spawnSync(executable, args, { stdio: "inherit" });
  if (child.error) {
    console.log(`::error::${executable} did not start: ${child.error.message}`);
    process.exit(1);
  }
  const ended = child.status === null ? { crashed: true, text: `signal ${child.signal}` } : describeExitCode(child.status);
  console.log(`${image} (process ${child.pid}) ended after ${((Date.now() - started) / 1000).toFixed(1)} s with ${ended.text}`);
  if (ended.crashed) {
    console.log(`::error::${image} crashed: ${ended.text}`);
    if (windows) {
      const report = resolve(dirname(fileURLToPath(import.meta.url)), "windows-crash-report.ps1");
      spawnSync("pwsh", ["-NoProfile", "-File", report, "-Since", started.toISOString(), "-ProcessId", String(child.pid),
        "-Image", image, "-DumpFolder", dumps], { stdio: "inherit", timeout: 240_000 });
    }
    process.exit(1);
  }
  process.exit(child.status);
}
