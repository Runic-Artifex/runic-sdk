import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { runInNewContext } from "node:vm";

const root = resolve(import.meta.dir, "../..");
const scripts = [
  "packages/dotnet/Runic.Application.Views.CsWebUi/www/runic-cswebui.js",
  "packages/dotnet/Runic.Application.Desktop/www/runic-desktop-views.js",
];

// A connected bridge that subscribes to all events cancels navigations,
// including history updates, until navigation is allowed again (#42).
for (const script of scripts) {
  test(`${script} allows history updates the bridge would cancel`, () => {
    const events = [];
    let allowed = false;
    const history = {
      pushState(state) { events.push(["pushState", allowed, state]); },
      replaceState(state) { events.push(["replaceState", allowed, state]); },
    };
    const webui = {
      isConnected: () => true,
      allowNavigation: status => { allowed = status; },
      call: async () => undefined,
    };
    const context = { history, setInterval, clearInterval, console };
    context.window = context;
    context.globalThis = context;
    runInNewContext(readFileSync(resolve(root, script), "utf8"), context);
    context.webui = webui;

    context.history.replaceState({ route: 1 }, "");
    allowed = false;
    context.history.pushState({ route: 2 }, "");

    expect(events).toEqual([["replaceState", true, { route: 1 }], ["pushState", true, { route: 2 }]]);
  });
}
