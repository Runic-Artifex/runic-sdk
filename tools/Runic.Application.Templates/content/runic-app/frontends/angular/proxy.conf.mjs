// The Angular development server's proxy configuration (angular.json, serve target, proxyConfig).
// runicDevSessionProxy() answers /__runic/state, which lets `dotnet runic dev` confirm that the server on its port is
// the one it started, and /__runic/diagnostics, where the Runic development client that src/main.ts loads reports the
// page's Views failures: the server prints them to this terminal. Add your own proxy entries next to it.
import { runicDevSessionProxy } from "@runic-artifex/vite-plugin-runic";

export default {
  ...runicDevSessionProxy(),
};
