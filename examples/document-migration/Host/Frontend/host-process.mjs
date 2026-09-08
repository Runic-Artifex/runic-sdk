import assert from "node:assert/strict";

// A successful UI assertion must not hide a crash or forced kill at shutdown.
export async function stopHost(host) {
  let forced = false;
  if (host.exitCode === null && host.signalCode === null) {
    await new Promise((resolve) => {
      const timer = setTimeout(() => {
        forced = true;
        host.kill("SIGKILL");
      }, 10000);
      host.once("exit", () => { clearTimeout(timer); resolve(); });
      if (host.stdin?.writable) host.stdin.end("stop\n");
      else host.kill("SIGINT");
    });
  }
  assert.equal(forced, false, "Host shutdown timed out");
  assert.equal(host.signalCode, null, `Host terminated by ${host.signalCode}`);
  assert.equal(host.exitCode, 0, "Host did not exit successfully");
}
