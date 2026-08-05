import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import {
  mkdir,
  readFile,
  rename,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import path from "node:path";

const [workspaceRoot, cacheDirectory, packageManager, lockFile, installCommand] =
  process.argv.slice(2);
if (
  !workspaceRoot ||
  !cacheDirectory ||
  !packageManager ||
  !lockFile ||
  !installCommand
) {
  throw new Error(
    "Usage: install-frontend.mjs WORKSPACE CACHE MANAGER LOCK_FILE INSTALL_COMMAND",
  );
}

const lockBytes = await readFile(lockFile);
const identity = `${packageManager}|${createHash("sha256")
  .update(lockBytes)
  .digest("hex")
  .toUpperCase()}`;
const stamp = path.join(cacheDirectory, "identity.txt");
const coordinatorLock = `${cacheDirectory}.lock`;
const nodeModules = path.join(workspaceRoot, "node_modules");
const deadline = Date.now() + 10 * 60 * 1000;

await mkdir(path.dirname(cacheDirectory), { recursive: true });
await acquire();
try {
  if ((await readIdentity()) === identity && (await exists(nodeModules))) {
    process.exitCode = 0;
  } else {
    const exitCode = await runInstall();
    if (exitCode !== 0) {
      process.exitCode = exitCode;
    } else {
      await mkdir(cacheDirectory, { recursive: true });
      const temporary = `${stamp}.${process.pid}.tmp`;
      await writeFile(temporary, `${identity}\n`, "utf8");
      await rename(temporary, stamp);
      process.exitCode = 0;
    }
  }
} finally {
  await rm(coordinatorLock, { recursive: true, force: true });
}

async function acquire() {
  for (;;) {
    try {
      await mkdir(coordinatorLock);
      return;
    } catch (error) {
      if (error?.code !== "EEXIST") {
        throw error;
      }

      const information = await stat(coordinatorLock).catch(() => null);
      if (
        information &&
        Date.now() - information.mtimeMs > 10 * 60 * 1000
      ) {
        await rm(coordinatorLock, { recursive: true, force: true });
        continue;
      }

      if (Date.now() >= deadline) {
        throw new Error(
          `Timed out waiting for frontend install lock '${coordinatorLock}'.`,
        );
      }

      await new Promise((resolve) => setTimeout(resolve, 100));
    }
  }
}

async function readIdentity() {
  try {
    return (await readFile(stamp, "utf8")).trim();
  } catch (error) {
    if (error?.code === "ENOENT") {
      return "";
    }

    throw error;
  }
}

async function exists(candidate) {
  try {
    await stat(candidate);
    return true;
  } catch (error) {
    if (error?.code === "ENOENT") {
      return false;
    }

    throw error;
  }
}

function runInstall() {
  return new Promise((resolve, reject) => {
    const child = spawn(installCommand, {
      cwd: workspaceRoot,
      shell: true,
      stdio: "inherit",
    });
    child.once("error", reject);
    child.once("exit", (code, signal) => {
      if (signal) {
        reject(new Error(`Frontend install terminated by signal ${signal}.`));
      } else {
        resolve(code ?? 1);
      }
    });
  });
}
