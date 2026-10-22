// The Angular development server's proxy configuration (angular.json, serve target, proxyConfig).
// runicDevSessionProxy() lets `dotnet runic dev` confirm that the server on its port is the one it started;
// without `dotnet runic dev` it adds nothing. Add your own proxy entries next to it.
import { runicDevSessionProxy } from "@runic-artifex/vite-plugin-runic";

export default {
  ...runicDevSessionProxy(),
};
