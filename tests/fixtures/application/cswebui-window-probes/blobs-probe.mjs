// Drives the CS-WebUI blob probe (CsWebUiWindowProbes --probe-blobs) from headless Chromium (blobs/1,
// specs/application/README.md, "Blobs and uploads"): the hello grants the connection an upload URL on the window's
// own loopback endpoint, the avatar's URL loads in an <img> and with fetch from the page's origin, a streamed upload
// with the connection's token is stored while one without it is refused, and the attach command reads the upload.
// RUNIC_PROBE_EXECUTABLE selects a published probe; otherwise the Release build runs with dotnet.
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { request as httpRequest } from "node:http";
import { networkInterfaces } from "node:os";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../../../../examples/shared/smoke.mjs";

const executable = process.env.RUNIC_PROBE_EXECUTABLE;
const dll = fileURLToPath(new URL("./bin/Release/net10.0/CsWebUiWindowProbes.dll", import.meta.url));
const host = spawn(executable ?? "dotnet", [...(executable ? [] : [dll]), "--probe-blobs"], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "", browser;
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
const exited = new Promise(resolve => host.on("exit", code => resolve(code)));
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, label) => waitFor(condition, { timeout: 20_000, label, detail });
const step = name => console.error(`blobs-probe: ${name}`);
// One request from outside the page: Node, with the headers given, to address:port. Resolves with the status and the
// CORS grant, or with { error } when the connection is refused.
const raw = (address, port, { method = "GET", path = "/", headers = {}, body } = {}) => new Promise(resolve => {
  const request = httpRequest({ host: address, port, method, path, headers, timeout: 5_000 }, response => {
    response.resume();
    response.on("end", () => resolve({ status: response.statusCode, allow: response.headers["access-control-allow-origin"] }));
  });
  request.on("error", error => resolve({ error: error.code ?? String(error) }));
  request.on("timeout", () => request.destroy(new Error("timeout")));
  if (body !== undefined) request.write(body);
  request.end();
});
const check = (condition, message, value) => {
  if (!condition) throw new Error(`${message}: ${JSON.stringify(value)}`);
};

try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Probe exited: ${errors}`);
    return output.match(/RUNIC_APPLICATION_URL=(\S+)/)?.[1];
  }, "probe URL");
  browser = await launchChromium(url, { profilePrefix: "runic-cswebui-blobs-", timeout: 20_000 });
  await retry(() => browser.evaluate("window.__runicBridge?.isConnected() === true"), "the CS-WebUI connection");
  const send = message => browser.evaluate(`(async () => JSON.parse(await window.__runicBridge.request(${JSON.stringify(JSON.stringify(message))})))()`);
  step("hello");
  const hello = await send({ kind: "hello", protocol: 4, session: randomUUID(), client: { name: "blobs-probe", version: "0" }, features: ["blobs/1"] });
  check(hello.ok === true && hello.features.includes("blobs/1") && hello.blobs?.token?.length === 32, "The hello's upload grant", hello);
  const page = new URL(url);
  const upload = new URL(hello.blobs.upload);
  check((upload.hostname === "localhost" || upload.hostname === "127.0.0.1") && upload.port !== page.port && upload.pathname === "/_runic/upload", "The upload URL", hello.blobs);

  step("open");
  const opened = await send({ kind: "open", route: "blobProbe", presentation: "p1" });
  check(opened.ok === true, "The open", opened);
  const avatar = opened.state.avatar;
  check(avatar.type === "image/png" && avatar.size > 0 && avatar.name === "avatar.png" && avatar.url.startsWith(`${upload.origin}/_runic/blobs/`),
    "The avatar", avatar);
  // An <img> loads it, and the page's fetch reads it across the port, with the window's CORS grant.
  step("img");
  const image = await browser.evaluate(`new Promise(resolve => {
    const image = new Image();
    image.onload = () => resolve({ width: image.naturalWidth, height: image.naturalHeight });
    image.onerror = () => resolve(null);
    image.src = ${JSON.stringify(avatar.url)};
  })`);
  check(image?.width === 1 && image?.height === 1, "The avatar in an <img>", image);
  step("fetch");
  const fetched = await browser.evaluate(`(async () => {
    const response = await fetch(${JSON.stringify(avatar.url)}, { credentials: "include" });
    return { status: response.status, type: response.headers.get("Content-Type"), disposition: response.headers.get("Content-Disposition"),
      size: (await response.arrayBuffer()).byteLength };
  })()`);
  check(fetched.status === 200 && fetched.type === "image/png" && fetched.size === avatar.size && fetched.disposition?.startsWith("inline"),
    "The avatar with fetch", fetched);

  const post = token => browser.evaluate(`(async () => {
    const headers = { "Content-Type": "text/plain", "X-Runic-Upload-Name": encodeURIComponent("probe.txt") };
    ${token === undefined ? "" : `headers["X-Runic-Upload-Token"] = ${JSON.stringify(token)};`}
    const response = await fetch(${JSON.stringify(upload.href)}, { method: "POST", credentials: "include", headers, body: new Blob(["x".repeat(1_000_000)]) });
    return { status: response.status, body: await response.json() };
  })()`);
  step("upload without token");
  const refused = await post(undefined);
  check(refused.status === 403 && refused.body.ok === false, "An upload without the token", refused);
  step("upload");
  const stored = await post(hello.blobs.token);
  check(stored.status === 201 && stored.body.upload.size === 1_000_000 && stored.body.upload.name === "probe.txt", "The upload", stored);

  // Requests from outside the page are refused: a foreign Host (DNS rebinding), another origin's preflight, upload and
  // fetch, and a peer of another machine.
  step("foreign requests");
  const endpoint = { address: upload.hostname, port: Number(upload.port) };
  const blobPath = new URL(avatar.url).pathname;
  const foreignHost = await raw(endpoint.address, endpoint.port, { path: blobPath, headers: { Host: `rebound.example:${endpoint.port}` } });
  check(foreignHost.status >= 400 && foreignHost.status < 500, "A blob request with a foreign Host", foreignHost);
  const evil = "http://attacker.example";
  const preflight = await raw(endpoint.address, endpoint.port, { method: "OPTIONS", path: upload.pathname,
    headers: { Origin: evil, "Access-Control-Request-Method": "POST", "Access-Control-Request-Headers": "x-runic-upload-token" } });
  check(preflight.status === 403 && preflight.allow !== evil, "Another origin's preflight", preflight);
  const crossPost = await raw(endpoint.address, endpoint.port, { method: "POST", path: upload.pathname,
    headers: { Origin: evil, "X-Runic-Upload-Token": hello.blobs.token, "Content-Type": "text/plain", "Content-Length": "4" }, body: "evil" });
  check(crossPost.status === 403, "Another origin's upload with the token", crossPost);
  const crossGet = await raw(endpoint.address, endpoint.port, { path: blobPath, headers: { Origin: evil } });
  check(crossGet.status === 403 && crossGet.allow !== evil, "Another origin's fetch of a blob", crossGet);
  const lan = Object.values(networkInterfaces()).flat().find(entry => entry && entry.family === "IPv4" && !entry.internal)?.address;
  if (lan !== undefined) {
    const remote = await raw(lan, endpoint.port, { path: blobPath, headers: { Host: `${upload.hostname}:${endpoint.port}` } });
    check(remote.error !== undefined || remote.status === 403, "A blob request over the machine's network address", remote);
  }

  // Uploads that the page aborts midway, more than the window receives at once, keep no slot: the next one is stored.
  step("aborted uploads");
  const abortedUploads = await browser.evaluate(`(async () => {
    const outcomes = [];
    for (let attempt = 0; attempt < 18; attempt++) {
      outcomes.push(await new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        xhr.open("POST", ${JSON.stringify(upload.href)});
        xhr.withCredentials = true;
        xhr.setRequestHeader("X-Runic-Upload-Token", ${JSON.stringify(hello.blobs.token)});
        xhr.upload.onprogress = () => xhr.abort();
        xhr.onabort = () => resolve("aborted");
        xhr.onload = () => resolve(xhr.status);
        xhr.onerror = () => resolve("error");
        xhr.send(new Blob(["y".repeat(3_000_000)]));
      }));
    }
    return outcomes;
  })()`);
  check(abortedUploads.length === 18, "The aborted uploads", abortedUploads);
  const afterAborts = await retry(async () => {
    const answer = await post(hello.blobs.token);
    return answer.status === 201 && answer;
  }, "an upload after the aborted ones");
  check(afterAborts.body.upload.size === 1_000_000, "The upload after the aborted ones", afterAborts);

  step("attach");
  const attached = await send({ kind: "command", route: "blobProbe", member: "attach", input: stored.body.upload });
  check(attached.ok === true, "The attach command", attached);
  const again = await send({ kind: "command", route: "blobProbe", member: "attach", input: stored.body.upload });
  check(again.ok === false && again.error?.reason === "invalid-input", "A second claim of the upload", again);

  host.stdin.end("\n");
  const code = await waitFor(async () => {
    const value = await Promise.race([exited, pause(200).then(() => undefined)]);
    return value === undefined ? false : { code: value };
  }, { timeout: 20_000, label: "the probe's exit", detail });
  if (code.code !== 0 || !output.includes("CSWEBUI_BLOBS_OK"))
    throw new Error(`The probe exited with ${code.code}:\n${tail(output)}\n${tail(errors)}`);
  console.log("CSWEBUI_BLOBS_PAGE_OK|grant|img|fetch|upload|token|foreign-host|cross-origin|remote|aborted|claim");
} catch (error) {
  console.error(await detail());
  throw error;
} finally {
  try { await browser?.close(); }
  finally {
    if (host.exitCode === null && !host.stdin.writableEnded) host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
