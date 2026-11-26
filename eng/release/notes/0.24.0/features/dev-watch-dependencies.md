## `dotnet runic dev` watches the library packages that the page imports

While `dev` runs, it watches every compiled frontend package that the page depends on: a client library's package, or a
workspace package that no .NET project owns. It reads them from `RunicViewsGetFrontendDependencies` after the build.

- **Watch.** A package with a `watch` script runs it. Otherwise `dev` reruns the package's build, debounced, when one of
  its files changes. `node_modules`, `bin`, `obj`, hidden folders and the package's output folders don't count. Vite
  reloads the page from the new output.
- **Failures keep the page working.** A rebuild that fails, or that doesn't write the package's exports, is reported as
  a `RAPPDEV1018` line in MSBuild's form, and `dev` puts the output folders of the last good build back. A watch script
  writes its own output, so `dev` reports its located errors (`src/x.ts(3,1): error TS1005: …`) the same way; with
  `tsc -w`, `noEmitOnError` keeps its last good output.
- **Fails at the start.** A package build that fails in `dev`'s initial build stops `dev` with `RAPPDEV1018` and the
  build's own error instead of `RAPPDEV1006`. A compiled package whose exports are missing after the build stops it
  too, naming the missing file and why, for example a package build that is off.
- **Fails fast.** When Vite answers the application entry with HTTP 500 twice, such as for an import it cannot resolve,
  `dev` stops at once with Vite's own error, and names the package and its state when the import is a dependency
  package. Before, it waited 30 s for a `RAPPDEV1007` timeout that pointed at `doctor`. A development server that exits
  reports the first error line of its output.
- **Repeated lines.** An identical line of the development server or of a package watcher, printed less than 10 s
  before, is held back and counted (`[vite] (repeated 1,106 more times) …`). One unresolved import used to print 1,107
  times.
- **Session file.** `dev-session.json` lists the packages as `dependencies`, each with its owner, kind, specifier, build
  and watch commands, exports, `state` (`built`, `building`, `failed`, `missing-output`, `source` or `not-built`),
  `watch` (`script`, `rebuild`, `vite` or `none`) and last failure. The schema stays
  `runic.application.tool.dev-session/1`, since the change only adds a property.

See [dotnet-runic](../../../tools/dotnet-runic/README.md#library-packages-under-dev).
