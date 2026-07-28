using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal sealed class ViteConfigurationBridge : IDisposable
{
    private readonly string _directory;

    private ViteConfigurationBridge(string directory, string configurationPath)
    {
        _directory = directory;
        ConfigurationPath = configurationPath;
    }

    internal string ConfigurationPath { get; }

    internal static ViteConfigurationBridge Create(
        DevProjectConfiguration configuration,
        Uri renderedFragmentsEndpoint)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(renderedFragmentsEndpoint);
        string directory = Path.Combine(
            Path.GetTempPath(),
            "webuitoolkit-vite",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "vite.config.mjs");
        try
        {
            File.WriteAllText(
                path,
                CreateSource(configuration, renderedFragmentsEndpoint),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
            return new ViteConfigurationBridge(directory, path);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    internal static string CreateSource(
        DevProjectConfiguration configuration,
        Uri renderedFragmentsEndpoint)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(renderedFragmentsEndpoint);
        string entryPath = Path.GetFullPath(
            configuration.ViteDevServerEntry.TrimStart('/'),
            configuration.FrontendPackageDirectory);
        string userConfigurationImport = string.IsNullOrWhiteSpace(
            configuration.ViteConfigurationPath)
            ? "const userConfigExport = {};"
            : $"import userConfigExport from {Json(new Uri(configuration.ViteConfigurationPath).AbsoluteUri)};";
        string diagnosticsPath = Json(configuration.CwhtmlDiagnosticsPath);
        string hotReloadPath = Json(configuration.CwhtmlHotReloadPath + ".ready");
        string normalizedEntry = Json(NormalizePath(entryPath));
        string fragmentInspectorEndpoint = configuration.CwhtmlEnabled
            ? Json(renderedFragmentsEndpoint.AbsoluteUri)
            : "undefined";

        return $$"""
            import { readFile } from "node:fs/promises";
            {{userConfigurationImport}}

            const diagnosticsPath = {{diagnosticsPath}};
            const hotReloadPath = {{hotReloadPath}};
            const entryPath = {{normalizedEntry}};
            const diagnosticsContract = "webuitoolkit.cwhtml.diagnostics/1.0";
            const hotReloadContract = "webuitoolkit.cwhtml.hot-reload/1.0";
            const renderedFragmentsContract =
              "webuitoolkit.cwhtml.rendered-fragments/1.0";
            const renderedFragmentsEndpoint = {{fragmentInspectorEndpoint}};
            const virtualClientId = "\0virtual:webuitoolkit-cwhtml-diagnostics";

            const normalizePath = value => value.replaceAll("\\", "/");
            const cleanModuleId = value => normalizePath(value.split("?", 1)[0]);

            function webuitoolkitDiagnosticsPlugin() {
              let server;
              let lastSnapshot;
              let publishTimer;
              let publishInterval;
              let lastHotReloadSnapshot;

              async function readSnapshot() {
                if (!diagnosticsPath) {
                  return { raw: "", diagnostics: [] };
                }

                try {
                  const raw = await readFile(diagnosticsPath, "utf8");
                  const snapshot = JSON.parse(raw);
                  if (snapshot?.contract !== diagnosticsContract ||
                      !Array.isArray(snapshot?.diagnostics)) {
                    throw new Error(`Expected ${diagnosticsContract}.`);
                  }
                  return { raw, diagnostics: snapshot.diagnostics };
                } catch (error) {
                  if (error?.code === "ENOENT") {
                    return { raw: "", diagnostics: [] };
                  }
                  return {
                    raw: `invalid:${error?.message ?? error}`,
                    diagnostics: [{
                      id: "WUTDEV1008",
                      severity: "error",
                      message: `Could not read the cwhtml diagnostics snapshot: ${error?.message ?? error}`,
                      logicalPath: diagnosticsPath,
                      filePath: diagnosticsPath,
                      range: null,
                    }],
                  };
                }
              }

              async function sourceFrame(diagnostic) {
                if (!diagnostic.range || !diagnostic.filePath) {
                  return undefined;
                }

                try {
                  const lines = (await readFile(diagnostic.filePath, "utf8")).split(/\r?\n/u);
                  const line = diagnostic.range.start.line;
                  const first = Math.max(0, line - 2);
                  const last = Math.min(lines.length - 1, line + 2);
                  const width = String(last + 1).length;
                  const output = [];
                  for (let index = first; index <= last; index += 1) {
                    const marker = index === line ? ">" : " ";
                    output.push(`${marker} ${String(index + 1).padStart(width)} | ${lines[index]}`);
                    if (index === line) {
                      const start = Math.max(0, diagnostic.range.start.column);
                      const end = diagnostic.range.end.line === line
                        ? Math.max(start + 1, diagnostic.range.end.column)
                        : start + 1;
                      output.push(
                        `  ${" ".repeat(width)} | ${" ".repeat(start)}${"^".repeat(end - start)}`);
                    }
                  }
                  return output.join("\n");
                } catch {
                  return undefined;
                }
              }

              async function readHotReloadSnapshot() {
                if (!hotReloadPath) {
                  return undefined;
                }
                try {
                  const raw = await readFile(hotReloadPath, "utf8");
                  const snapshot = JSON.parse(raw);
                  if (snapshot?.contract !== hotReloadContract ||
                      !Array.isArray(snapshot?.templates)) {
                    throw new Error(`Expected ${hotReloadContract}.`);
                  }
                  return snapshot;
                } catch (error) {
                  if (error?.code === "ENOENT") {
                    return undefined;
                  }
                  throw error;
                }
              }

              async function errorPayload(errors) {
                const first = errors[0];
                const location = first.range
                  ? {
                      file: first.filePath,
                      line: first.range.start.line + 1,
                      column: first.range.start.column,
                    }
                  : undefined;
                const stack = errors.map(diagnostic => {
                  const position = diagnostic.range
                    ? `(${diagnostic.range.start.line + 1},${diagnostic.range.start.column + 1})`
                    : "";
                  return `${diagnostic.logicalPath}${position}: ${diagnostic.severity} ` +
                    `${diagnostic.id}: ${diagnostic.message}`;
                }).join("\n");
                return {
                  name: "CwhtmlCompilerError",
                  message: `[${first.id}] ${first.message}`,
                  stack,
                  id: first.filePath,
                  frame: await sourceFrame(first),
                  plugin: "webuitoolkit:cwhtml",
                  loc: location,
                };
              }

              async function publish(force = false) {
                const snapshot = await readSnapshot();
                if (!force && snapshot.raw === lastSnapshot) {
                  return;
                }
                lastSnapshot = snapshot.raw;
                const errors = snapshot.diagnostics.filter(
                  diagnostic => diagnostic.severity === "error");
                server.ws.send({
                  type: "custom",
                  event: "webuitoolkit:cwhtml-diagnostics-state",
                  data: {
                    state: errors.length === 0 ? "clear" : "error",
                    diagnostics: errors,
                  },
                });
                if (errors.length === 0) {
                  server.ws.send({
                    type: "custom",
                    event: "webuitoolkit:cwhtml-diagnostics-clear",
                    data: {},
                  });
                  return;
                }
                server.ws.send({ type: "error", err: await errorPayload(errors) });
              }

              async function publishHotReload() {
                const snapshot = await readHotReloadSnapshot();
                if (!snapshot) {
                  return;
                }
                if (!lastHotReloadSnapshot) {
                  lastHotReloadSnapshot = snapshot;
                  return;
                }
                const previous = new Map(
                  lastHotReloadSnapshot.templates.map(template =>
                    [template.logicalPath, template]));
                const fragments = new Set();
                for (const template of snapshot.templates) {
                  const prior = previous.get(template.logicalPath);
                  if (prior?.rendererSha256 !== template.rendererSha256) {
                    for (const fragment of template.affectedFragments ?? []) {
                      fragments.add(fragment);
                    }
                  }
                }
                lastHotReloadSnapshot = snapshot;
                if (fragments.size !== 0) {
                  server.ws.send({
                    type: "custom",
                    event: "webuitoolkit:cwhtml-fragments",
                    data: { fragments: Array.from(fragments).sort() },
                  });
                }
              }

              async function publishFragmentHandles() {
                const snapshot = await readHotReloadSnapshot();
                if (!snapshot) {
                  return;
                }
                const fragments = new Set();
                for (const template of snapshot.templates) {
                  for (const fragment of template.affectedFragments ?? []) {
                    fragments.add(fragment);
                  }
                }
                server.ws.send({
                  type: "custom",
                  event: "webuitoolkit:cwhtml-fragment-handles",
                  data: { fragments: Array.from(fragments).sort() },
                });
              }

              function schedulePublish(changedPath) {
                const path = cleanModuleId(changedPath);
                if (diagnosticsPath && path === normalizePath(diagnosticsPath)) {
                  clearTimeout(publishTimer);
                  publishTimer = setTimeout(() => void publish(), 25);
                }
                if (hotReloadPath && path === normalizePath(hotReloadPath)) {
                  setTimeout(() => void publishHotReload(), 25);
                }
              }

              return {
                name: "webuitoolkit:cwhtml-diagnostics",
                enforce: "pre",
                configureServer(viteServer) {
                  server = viteServer;
                  if (diagnosticsPath) {
                    server.watcher.add(diagnosticsPath);
                    server.watcher.on("add", schedulePublish);
                    server.watcher.on("change", schedulePublish);
                    server.watcher.on("unlink", schedulePublish);
                    publishInterval = setInterval(() => void publish(), 250);
                  }
                  if (hotReloadPath) {
                    server.watcher.add(hotReloadPath);
                    void publishHotReload();
                  }
                  server.ws.on(
                    "webuitoolkit:cwhtml-diagnostics-ready",
                    () => {
                      void publish(true);
                      void publishFragmentHandles();
                    });
                  server.httpServer?.once("close", () => {
                    clearTimeout(publishTimer);
                    clearInterval(publishInterval);
                  });
                },
                resolveId(id) {
                  return id === "virtual:webuitoolkit-cwhtml-diagnostics"
                    ? virtualClientId
                    : undefined;
                },
                load(id) {
                  if (id !== virtualClientId) {
                    return undefined;
                  }
                  return `
                    const renderedFragmentsEndpoint = ${JSON.stringify(renderedFragmentsEndpoint)};
                    const renderedFragmentsContract =
                      "webuitoolkit.cwhtml.rendered-fragments/1.0";
                    const inspectedFragments = new Set();
                    let inspectionTimer;
                    let inspectionQueue = Promise.resolve();

                    function scheduleRenderedFragmentInspection() {
                      if (!renderedFragmentsEndpoint || inspectedFragments.size === 0) {
                        return;
                      }
                      clearTimeout(inspectionTimer);
                      inspectionTimer = setTimeout(() => {
                        inspectionQueue = inspectionQueue.then(async () => {
                          const fragments = [];
                          for (const handle of inspectedFragments) {
                            const element = document.getElementById(handle);
                            if (element && element.outerHTML.length <= 262144) {
                              fragments.push({ handle, html: element.outerHTML });
                            }
                          }
                          await fetch(renderedFragmentsEndpoint, {
                            method: "POST",
                            mode: "no-cors",
                            headers: { "Content-Type": "text/plain;charset=UTF-8" },
                            body: JSON.stringify({
                              contract: renderedFragmentsContract,
                              fragments,
                            }),
                          });
                        }).catch(() => {});
                      }, 40);
                    }

                    document.addEventListener(
                      "htmx:afterSettle",
                      scheduleRenderedFragmentInspection);
                    if (import.meta.hot) {
                      import.meta.hot.on(
                        "webuitoolkit:cwhtml-fragment-handles",
                        update => {
                          inspectedFragments.clear();
                          for (const fragment of update?.fragments ?? []) {
                            if (/^[A-Za-z][A-Za-z0-9_-]{0,63}$/u.test(fragment)) {
                              inspectedFragments.add(fragment);
                            }
                          }
                          scheduleRenderedFragmentInspection();
                        });
                      import.meta.hot.on(
                        "webuitoolkit:cwhtml-diagnostics-state",
                        state => {
                          globalThis.__webuitoolkitCwhtmlDiagnostics = state;
                        });
                      import.meta.hot.on(
                        "webuitoolkit:cwhtml-diagnostics-clear",
                        () => document.querySelector("vite-error-overlay")?.remove());
                      import.meta.hot.on(
                        "webuitoolkit:cwhtml-fragments",
                        async update => {
                          const fragments = Array.isArray(update?.fragments)
                            ? update.fragments
                            : [];
                          for (const fragment of fragments) {
                            if (!/^[A-Za-z][A-Za-z0-9_-]{0,63}$/u.test(fragment) ||
                                !document.getElementById(fragment)) {
                              continue;
                            }
                            await globalThis.htmx.ajax(
                              "GET",
                              "/_webui/htmx/dev-refresh/" + fragment,
                              { target: "#" + fragment, swap: "outerHTML" });
                          }
                          globalThis.__webuitoolkitCwhtmlHotReload = {
                            state: "refreshed",
                            fragments,
                          };
                          scheduleRenderedFragmentInspection();
                        });
                      import.meta.hot.send("webuitoolkit:cwhtml-diagnostics-ready");
                    }
                  `;
                },
                transform(code, id) {
                  if (cleanModuleId(id) !== entryPath) {
                    return undefined;
                  }
                  return {
                    code: `import "virtual:webuitoolkit-cwhtml-diagnostics";\n${code}`,
                    map: null,
                  };
                },
              };
            }

            async function resolveUserConfig(environment) {
              const candidate = typeof userConfigExport === "function"
                ? await userConfigExport(environment)
                : await userConfigExport;
              return candidate ?? {};
            }

            export default async environment => {
              const userConfig = await resolveUserConfig(environment);
              return {
                ...userConfig,
                plugins: [
                  ...(Array.isArray(userConfig.plugins) ? userConfig.plugins : []),
                  webuitoolkitDiagnosticsPlugin(),
                ],
              };
            };
            """;
    }

    private static string Json(string value) =>
        string.Concat('"', JsonEncodedText.Encode(value).ToString(), '"');

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
