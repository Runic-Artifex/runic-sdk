using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RunicToolkit.DotNet.RunicToolkit;

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
            "runic-toolkit-vite",
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
        string compilerDiagnosticsPath = configuration.FrontendCompilerEnabled
            ? Json(configuration.FrontendCompilerDiagnosticsPath)
            : "undefined";
        string compilerHotReloadPath = configuration.FrontendCompilerEnabled
            ? Json(configuration.FrontendCompilerHotReloadPath + ".ready")
            : "undefined";
        string normalizedEntry = Json(NormalizePath(entryPath));
        string fragmentInspectorEndpoint = configuration.HasFrontendCompiler
            ? Json(renderedFragmentsEndpoint.AbsoluteUri)
            : "undefined";

        return $$"""
            import { readFile } from "node:fs/promises";
            {{userConfigurationImport}}

            const diagnosticsSources = [
              {
                path: {{compilerDiagnosticsPath}},
                contract: "runic-toolkit.frontend-compiler.diagnostics/1.0",
              },
            ].filter(source => source.path);
            const hotReloadSources = [
              {
                path: {{compilerHotReloadPath}},
                contract: "runic-toolkit.frontend-compiler.hot-reload/1.0",
              },
            ].filter(source => source.path);
            const entryPath = {{normalizedEntry}};
            const renderedFragmentsContract =
              "runic-toolkit.frontend-compiler.rendered-fragments/1.0";
            const renderedFragmentsEndpoint = {{fragmentInspectorEndpoint}};
            const virtualClientId = "\0virtual:runic-toolkit-frontend-compiler-diagnostics";

            const normalizePath = value => value.replaceAll("\\", "/");
            const cleanModuleId = value => normalizePath(value.split("?", 1)[0]);

            function runicToolkitDiagnosticsPlugin() {
              let server;
              let lastSnapshot;
              let publishTimer;
              let publishInterval;
              let lastHotReloadSnapshot;

              async function readSnapshot() {
                if (diagnosticsSources.length === 0) {
                  return { raw: "", diagnostics: [] };
                }

                const rawParts = [];
                const diagnostics = [];
                for (const source of diagnosticsSources) {
                  try {
                    const raw = await readFile(source.path, "utf8");
                    const snapshot = JSON.parse(raw);
                    if (snapshot?.contract !== source.contract ||
                        !Array.isArray(snapshot?.diagnostics)) {
                      throw new Error(`Expected ${source.contract}.`);
                    }
                    rawParts.push(`${source.contract}:${raw}`);
                    diagnostics.push(...snapshot.diagnostics);
                  } catch (error) {
                    if (error?.code === "ENOENT") {
                      rawParts.push(`${source.contract}:missing`);
                      continue;
                    }
                    rawParts.push(`${source.contract}:invalid:${error?.message ?? error}`);
                    diagnostics.push({
                      id: "RTKDEV1008",
                      severity: "error",
                      message: `Could not read the compiled-markup diagnostics snapshot: ${error?.message ?? error}`,
                      logicalPath: source.path,
                      filePath: source.path,
                      range: null,
                    });
                  }
                }
                return { raw: rawParts.join("\n"), diagnostics };
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
                if (hotReloadSources.length === 0) {
                  return undefined;
                }

                const templates = [];
                for (const source of hotReloadSources) {
                  try {
                    const raw = await readFile(source.path, "utf8");
                    const snapshot = JSON.parse(raw);
                    if (snapshot?.contract !== source.contract ||
                        !Array.isArray(snapshot?.templates)) {
                      throw new Error(`Expected ${source.contract}.`);
                    }
                    templates.push(...snapshot.templates.map(template => ({
                      ...template,
                      sourceContract: source.contract,
                    })));
                  } catch (error) {
                    if (error?.code === "ENOENT") {
                      continue;
                    }
                    throw error;
                  }
                }
                return templates.length === 0 ? undefined : { templates };
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
                  name: "FrontendCompilerCompilerError",
                  message: `[${first.id}] ${first.message}`,
                  stack,
                  id: first.filePath,
                  frame: await sourceFrame(first),
                  plugin: "runic-toolkit:frontend-compiler",
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
                  event: "runic-toolkit:frontend-compiler-diagnostics-state",
                  data: {
                    state: errors.length === 0 ? "clear" : "error",
                    diagnostics: errors,
                  },
                });
                if (errors.length === 0) {
                  server.ws.send({
                    type: "custom",
                    event: "runic-toolkit:frontend-compiler-diagnostics-clear",
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
                    [`${template.sourceContract}:${template.logicalPath}`, template]));
                const fragments = new Set();
                for (const template of snapshot.templates) {
                  const prior = previous.get(
                    `${template.sourceContract}:${template.logicalPath}`);
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
                    event: "runic-toolkit:frontend-compiler-fragments",
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
                  event: "runic-toolkit:frontend-compiler-fragment-handles",
                  data: { fragments: Array.from(fragments).sort() },
                });
              }

              function schedulePublish(changedPath) {
                const path = cleanModuleId(changedPath);
                if (diagnosticsSources.some(
                    source => path === normalizePath(source.path))) {
                  clearTimeout(publishTimer);
                  publishTimer = setTimeout(() => void publish(), 25);
                }
                if (hotReloadSources.some(
                    source => path === normalizePath(source.path))) {
                  setTimeout(() => void publishHotReload(), 25);
                }
              }

              return {
                name: "runic-toolkit:frontend-compiler-diagnostics",
                enforce: "pre",
                configureServer(viteServer) {
                  server = viteServer;
                  if (diagnosticsSources.length !== 0) {
                    for (const source of diagnosticsSources) {
                      server.watcher.add(source.path);
                    }
                    server.watcher.on("add", schedulePublish);
                    server.watcher.on("change", schedulePublish);
                    server.watcher.on("unlink", schedulePublish);
                    publishInterval = setInterval(() => void publish(), 250);
                  }
                  for (const source of hotReloadSources) {
                    server.watcher.add(source.path);
                  }
                  if (hotReloadSources.length !== 0) {
                    void publishHotReload();
                  }
                  server.ws.on(
                    "runic-toolkit:frontend-compiler-diagnostics-ready",
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
                  return id === "virtual:runic-toolkit-frontend-compiler-diagnostics"
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
                      "runic-toolkit.frontend-compiler.rendered-fragments/1.0";
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
                      "runic-toolkit:frontend-compiler-settled",
                      scheduleRenderedFragmentInspection);
                    if (import.meta.hot) {
                      import.meta.hot.on(
                        "runic-toolkit:frontend-compiler-fragment-handles",
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
                        "runic-toolkit:frontend-compiler-diagnostics-state",
                        state => {
                          globalThis.__runicToolkitFrontendCompilerDiagnostics = state;
                        });
                      import.meta.hot.on(
                        "runic-toolkit:frontend-compiler-diagnostics-clear",
                        () => document.querySelector("vite-error-overlay")?.remove());
                      import.meta.hot.on(
                        "runic-toolkit:frontend-compiler-fragments",
                        update => {
                          const fragments = Array.isArray(update?.fragments)
                            ? update.fragments
                            : [];
                          globalThis.dispatchEvent(new CustomEvent(
                            "runic-toolkit:frontend-compiler-refresh",
                            { detail: { fragments } }));
                          globalThis.__runicToolkitFrontendCompilerHotReload = {
                            state: "refreshed",
                            fragments,
                          };
                          scheduleRenderedFragmentInspection();
                        });
                      import.meta.hot.send("runic-toolkit:frontend-compiler-diagnostics-ready");
                    }
                  `;
                },
                transform(code, id) {
                  if (cleanModuleId(id) !== entryPath) {
                    return undefined;
                  }
                  return {
                    code: `import "virtual:runic-toolkit-frontend-compiler-diagnostics";\n${code}`,
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
                  runicToolkitDiagnosticsPlugin(),
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
