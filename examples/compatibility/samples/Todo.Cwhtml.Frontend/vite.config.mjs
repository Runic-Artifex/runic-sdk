import { defineConfig } from "vite";
import { resolve } from "node:path";

const packageDirectory = import.meta.dirname;
const repositoryRoot = resolve(packageDirectory, "../..");
const nugetPackages = process.env.NUGET_PACKAGES ??
  resolve(repositoryRoot, ".packages/nuget");
const htmxAssets = resolve(
  nugetPackages,
  "runicmarkup.runictoolkit.htmx.js/0.1.0-preview.9.1/contentFiles/any/any/wwwroot/_content/RunicMarkup.RunicToolkit.Htmx.Js",
);

export default defineConfig({
  root: packageDirectory,
  appType: "custom",
  publicDir: false,
  resolve: {
    alias: {
      "runic-markup-htmx-cswebui": resolve(
        nugetPackages,
        "runicmarkup.runictoolkit.htmx.cswebui/0.1.0-preview.9.1/contentFiles/any/any/wwwroot/_content/RunicMarkup.RunicToolkit.Htmx.CsWebUi/runic-markup-htmx-cswebui-1.0.0.js",
      ),
      "runic-markup-htmx-csp": resolve(htmxAssets, "htmx-csp-2.0.10.js"),
      "runic-markup-htmx": resolve(htmxAssets, "runic-markup-htmx-1.0.0.mjs"),
    },
  },
});
