// Checks that a generated client resumes after its host transport reconnects
// without a page reload: it re-reads live routes and re-acknowledges mounted
// presentations with their existing token, retrying while .NET still assigns
// the token to the former connection.
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

type Call = { readonly route: string; readonly args: readonly unknown[] };

const [generatedDirectory] = Bun.argv.slice(2);
if (!generatedDirectory) throw new Error("Usage: GeneratedReconnectClientHarness.ts <generated-dir>");
const host = globalThis as typeof globalThis & { window?: Record<string, unknown> };
host.window = host as unknown as Record<string, unknown>;

const calls: Call[] = [];
const mountReplies = ["ok", "ignored", "ok"];
let reconnect: (() => void) | undefined;
let title = "before";
let revision = 3;
host.window.__runicBridge = {
  isConnected: () => true,
  onReconnect(listener: () => void) {
    reconnect = listener;
    return () => { reconnect = undefined; };
  },
  async call(route: string, ...args: unknown[]) {
    calls.push({ route, args });
    switch (route) {
      case "contentreconnectSnapshot":
        return JSON.stringify({ ok: true, state: { revision, title, __runicFields: { title: { version: 0 } } }, error: null });
      case "contentreconnectMount": return mountReplies.shift() ?? "unexpected";
      case "contentreconnectUnmount": return "ok";
      default: throw new Error(`Unexpected generated-client route ${route}.`);
    }
  },
};

const child = await import(pathToFileURL(resolve(generatedDirectory, "child.ts")).href);
const view = await child.pageChild("reconnect").connect();
expect(reconnect !== undefined, "The generated client did not observe host reconnects.");
const titles: string[] = [];
view.subscribe((state: { readonly title: string }) => titles.push(state.title));
const mounts = () => calls.filter(call => call.route === "contentreconnectMount");
const token = mounts()[0]?.args[0];
expect(typeof token === "string", "The presentation did not mount with a token.");

title = "after";
revision = 9;
reconnect!();
const deadline = Date.now() + 5_000;
while ((mounts().length < 3 || !titles.includes("after")) && Date.now() < deadline)
  await new Promise<void>(resolve => setTimeout(resolve, 20));
expect(titles.at(-1) === "after", "A reconnect did not refresh the route's state.");
expect(mounts().length === 3 && mounts().every(call => call.args[0] === token),
  "A reconnect did not re-acknowledge the mounted presentation with its token until it was accepted.");
view.dispose();
console.log("GENERATED_RECONNECT_CLIENT_OK");

function expect(condition: boolean, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
