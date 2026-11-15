// The template's WebView2 script debugging (D066.25 3): the launch profile's variable, the VS Code configurations and
// the endpoint they agree on. webview2-debug-smoke.mjs proves the endpoint against a running WebView2 on Windows;
// tests/engineering/template-packaging.test.mjs checks generated projects with the same rules on every OS.
// Plain JavaScript without assertions so that Node and Bun both import it; each check throws an Error.

export const variable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";

// WebView2's DevTools Protocol endpoint for --remote-debugging-port: Chromium listens on IPv4 loopback, and dotnet runic
// dev's session file lists it in this form (debugEndpoints, tools/dotnet-runic/DevSession.cs).
export const cdpEndpoint = port => `http://127.0.0.1:${port}/`;

// A template JSON file as `dotnet new runic-app` generates it for Runic Desktop, but for the project name: the
// //#if (<symbol>), //#else and //#endif blocks evaluated with `symbols` (desktopHost by default), then whole-line
// comments removed, as the template's JSONC has them. A symbol missing from `symbols` throws.
export function parseTemplateJson(text, symbols = { desktopHost: true }) {
  const kept = [], open = [];
  for (const line of text.split("\n")) {
    const directive = /^\s*\/\/#(if|else|endif)\b\s*(?:\((\w+)\))?\s*$/.exec(line);
    if (directive) {
      const [, kind, symbol] = directive;
      if (kind === "if") {
        if (!(symbol in symbols)) throw new Error(`The template condition ${line.trim()} has no value here.`);
        open.push(Boolean(symbols[symbol]));
      } else if (kind === "else") open.push(!open.pop());
      else open.pop();
    } else if (open.every(Boolean) && !/^\s*\/\//.test(line)) kept.push(line);
  }
  if (open.length) throw new Error("The template has an unterminated //#if.");
  return JSON.parse(kept.join("\n"));
}

const fail = message => { throw new Error(`WebView2 script debugging: ${message}`); };
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);

// js-debug's msedge attach (VS Code's ms-vscode.js-debug 1.140): it reads webSocketDebuggerUrl from
// http://<address>:<port>/json/version (address "localhost" by default, which it retries on the other loopback family),
// discovers the targets and attaches to the first one with type "page" whose URL is not devtools:// and matches
// urlFilter or url when either is set. useWebView: true keeps that port attach; an object would use a named pipe.
export function attachSelects(attach, target) {
  return target.type === "page" && !target.url.startsWith("devtools://") && !attach.url && !attach.urlFilter;
}

/**
 * Reads the WebView2 script-debugging profile and, with `launch`, the VS Code configurations, and checks that they agree:
 * the profile's variable enables the DevTools Protocol on one port, the Windows launch configuration sets the same
 * variables, and the WebView2 attach configuration reaches that port's endpoint and selects any page target of it.
 * Returns the profile's name, its variables, the port and the endpoint.
 */
export function webView2Debugging(launchSettings, launch) {
  const entries = Object.entries(launchSettings.profiles ?? {}).filter(([, profile]) => profile.jsWebView2Debugging === true);
  if (entries.length !== 1) fail(`expected one launch profile with jsWebView2Debugging, found ${entries.length}`);
  const [profileName, profile] = entries[0];
  if (profile.commandName !== "Project") fail(`${profileName} has commandName ${profile.commandName}, not Project`);
  const environment = profile.environmentVariables ?? {};
  const port = Number(/(?:^|\s)--remote-debugging-port=(\d+)(?:\s|$)/.exec(environment[variable] ?? "")?.[1]);
  if (!(port > 0 && port <= 65535)) fail(`${profileName}'s ${variable} names no --remote-debugging-port: ${environment[variable]}`);
  const result = { profileName, environment, port, endpoint: cdpEndpoint(port) };
  if (!launch) return result;

  const windows = launch.configurations.filter(entry => entry.type === "coreclr" && entry.request === "launch" && entry.env?.[variable]);
  if (windows.length !== 1) fail(`expected one coreclr launch with ${variable}, found ${windows.length}`);
  if (!same(windows[0].env, environment)) fail(`"${windows[0].name}" sets ${JSON.stringify(windows[0].env)}, the profile ${JSON.stringify(environment)}`);
  const attaches = launch.configurations.filter(entry => entry.type === "msedge" && entry.request === "attach");
  if (attaches.length !== 1) fail(`expected one msedge attach configuration, found ${attaches.length}`);
  const [attach] = attaches;
  if (attach.useWebView !== true) fail(`"${attach.name}" has useWebView ${JSON.stringify(attach.useWebView)}, not true (the port attach)`);
  if (attach.port !== port) fail(`"${attach.name}" attaches to port ${attach.port}, the profile enables ${port}`);
  const address = attach.address ?? "localhost";
  if (!["localhost", "127.0.0.1"].includes(address)) fail(`"${attach.name}" attaches to ${address}; WebView2 listens on 127.0.0.1`);
  // The page's loopback origin changes with every start, so no url or urlFilter could name it in advance.
  if (attach.url || attach.urlFilter) fail(`"${attach.name}" filters targets by ${attach.url ?? attach.urlFilter}`);
  return { ...result, windowConfiguration: windows[0].name, attach };
}
