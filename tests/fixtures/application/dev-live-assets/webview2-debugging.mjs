// The template's WebView2 script debugging (D066.25 3): the VS Code configurations and the endpoint they agree on. The
// template has no launch profile for it since 0.22 (D070.18): Visual Studio's JavaScript adapter dropped its variables. webview2-debug-smoke.mjs proves the endpoint against a running WebView2 on Windows;
// tests/engineering/template-packaging.test.mjs checks generated projects with the same rules on every OS.
// Plain JavaScript without assertions so that Node and Bun both import it; each check throws an Error.

export const variable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";

// WebView2's DevTools Protocol endpoint for --remote-debugging-port: Chromium listens on IPv4 loopback, and dotnet runic
// dev's session file lists it in this form (debugEndpoints, tools/dotnet-runic/DevSession.cs).
export const cdpEndpoint = port => `http://127.0.0.1:${port}/`;

// A template JSON file as `dotnet new runic-app` generates it for Runic Desktop, but for the project name: the
// //#if (<symbol>), //#if (<symbol> == "<value>"), //#else and //#endif blocks evaluated with `symbols` (desktopHost by
// default), then whole-line comments removed, as the template's JSONC has them. A symbol missing from `symbols` throws.
export function parseTemplateJson(text, symbols = { desktopHost: true }) {
  const kept = [], open = [];
  for (const line of text.split("\n")) {
    const directive = /^\s*\/\/#(if|else|endif)\b\s*(?:\((\w+)(?:\s*(==|!=)\s*"([^"]*)")?\))?\s*$/.exec(line);
    if (directive) {
      const [, kind, symbol, operator, value] = directive;
      if (kind === "if") {
        if (!(symbol in symbols)) throw new Error(`The template condition ${line.trim()} has no value here.`);
        open.push(operator === undefined ? Boolean(symbols[symbol]) : (symbols[symbol] === value) === (operator === "=="));
      } else if (kind === "else") open.push(!open.pop());
      else open.pop();
    } else if (open.every(Boolean) && !/^\s*\/\//.test(line)) kept.push(line);
  }
  if (open.length) throw new Error("The template has an unterminated //#if.");
  return JSON.parse(kept.join("\n"));
}

const fail = message => { throw new Error(`WebView2 script debugging: ${message}`); };

// js-debug's msedge attach (VS Code's ms-vscode.js-debug 1.140): it reads webSocketDebuggerUrl from
// http://<address>:<port>/json/version (address "localhost" by default, which it retries on the other loopback family),
// discovers the targets and attaches to the first one with type "page" whose URL is not devtools:// and matches
// urlFilter or url when either is set. useWebView: true keeps that port attach; an object would use a named pipe.
export function attachSelects(attach, target) {
  return target.type === "page" && !target.url.startsWith("devtools://") && !attach.url && !attach.urlFilter;
}

/**
 * Reads the VS Code configurations and checks that they agree: the Windows launch configuration's variable enables the
 * DevTools Protocol on one port, and the WebView2 attach configuration reaches that port's endpoint and selects any page
 * target of it. With `launchSettings`, also checks that no launch profile is a WebView2 script-debugging profile
 * (`jsWebView2Debugging` or WebView2's variable; D070.18). Returns the launch configuration's name and variables, the
 * port, the endpoint and the attach configuration.
 */
export function webView2Debugging(launch, launchSettings) {
  if (launchSettings) {
    for (const [name, profile] of Object.entries(launchSettings.profiles ?? {})) {
      if ("jsWebView2Debugging" in profile || profile.environmentVariables?.[variable] !== undefined)
        fail(`the launch profile ${name} is a WebView2 script-debugging profile, which the template no longer has (D070.18)`);
    }
  }
  const windows = launch.configurations.filter(entry => entry.type === "coreclr" && entry.request === "launch" && entry.env?.[variable]);
  if (windows.length !== 1) fail(`expected one coreclr launch with ${variable}, found ${windows.length}`);
  const [window] = windows;
  const environment = window.env;
  const port = Number(/(?:^|\s)--remote-debugging-port=(\d+)(?:\s|$)/.exec(environment[variable])?.[1]);
  if (!(port > 0 && port <= 65535)) fail(`"${window.name}"'s ${variable} names no --remote-debugging-port: ${environment[variable]}`);
  const attaches = launch.configurations.filter(entry => entry.type === "msedge" && entry.request === "attach");
  if (attaches.length !== 1) fail(`expected one msedge attach configuration, found ${attaches.length}`);
  const [attach] = attaches;
  if (attach.useWebView !== true) fail(`"${attach.name}" has useWebView ${JSON.stringify(attach.useWebView)}, not true (the port attach)`);
  if (attach.port !== port) fail(`"${attach.name}" attaches to port ${attach.port}, "${window.name}" enables ${port}`);
  const address = attach.address ?? "localhost";
  if (!["localhost", "127.0.0.1"].includes(address)) fail(`"${attach.name}" attaches to ${address}; WebView2 listens on 127.0.0.1`);
  // The page's loopback origin changes with every start, so no url or urlFilter could name it in advance.
  if (attach.url || attach.urlFilter) fail(`"${attach.name}" filters targets by ${attach.url ?? attach.urlFilter}`);
  return { windowConfiguration: window.name, environment, port, endpoint: cdpEndpoint(port), attach };
}
