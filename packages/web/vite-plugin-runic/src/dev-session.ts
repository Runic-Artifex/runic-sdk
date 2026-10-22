import type { ServerResponse } from "node:http";
import type { Plugin, ProxyOptions } from "vite";

// `dotnet runic dev` gives the development server process a random session value and accepts the server as ready
// only when /__runic/state answers with it, so another process that takes the port first is not mistaken for this
// server.
export const devSessionVariable = "RUNIC_APPLICATION_DEV_SESSION";
export const devSessionHeader = "X-Runic-Dev-Session";
const statePath = "/__runic/state";

/** The session value `dotnet runic dev` set, when it has the shape the tool writes: 32 to 128 hex digits. */
export function devSessionValue(value: string | undefined): string | undefined {
  return value !== undefined && /^[0-9a-f]{32,128}$/.test(value) ? value : undefined;
}

function answerSession(response: ServerResponse, session: string): void {
  response.statusCode = 204;
  response.setHeader("Cache-Control", "no-store");
  response.setHeader(devSessionHeader, session);
  response.end();
}

/**
 * A Vite plugin that only answers `/__runic/state` with the `dotnet runic dev` session, for a configuration that
 * cannot use `runic()`: SvelteKit, which warns about `runic()`'s `transformIndexHtml` hook. `runic()` already
 * answers it. Without `dotnet runic dev` the plugin does nothing.
 */
export function runicDevSession(): Plugin {
  return {
    name: "runic:dev-session",
    configureServer(server) {
      const session = devSessionValue(process.env[devSessionVariable]);
      if (session === undefined) return;
      server.middlewares.use(statePath, (_request, response) => answerSession(response, session));
    },
  };
}

/**
 * Vite proxy entries that answer `/__runic/state` with the `dotnet runic dev` session, for a development server
 * that takes a Vite proxy configuration but no Vite plugins: the Angular CLI's `proxyConfig`. Spread the result
 * into the proxy configuration. Without `dotnet runic dev` it is empty and changes nothing.
 */
export function runicDevSessionProxy(): Record<string, ProxyOptions> {
  const session = devSessionValue(process.env[devSessionVariable]);
  if (session === undefined) return {};
  return {
    "^/__runic/state(?:[/?#]|$)": {
      // The entry never proxies: it answers in bypass, and Vite then ends the request.
      bypass(request, response) {
        if (response === undefined) return false;
        answerSession(response, session);
        return request.url ?? statePath;
      },
    },
  };
}
