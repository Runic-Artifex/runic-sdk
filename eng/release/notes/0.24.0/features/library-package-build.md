## Library packages build with the application

A client library's frontend package is built by the library's build when its `package.json` has a `build` script, so
an application that imports a compiled package (`exports` into `dist/`) builds from a fresh clone with plain
`dotnet build`. `RunicApplicationFrontendBuildEnabled` is now `auto` in a client library; `false` still turns the
package build off. After the build, the generated `.contract.d.ts` files and their maps are copied to the folder of the
package's `./generated` export, which the guide described as a manual step, and every file that the package exports must
exist (`RUNICBRIDGE088`, naming the library).

- **Workspace packages that no .NET project owns**, such as a component or tokens package, are built too, with the
  workspace's package manager, dependencies first, by the project whose frontend depends on them.
- **Incremental.** A package builds only when one of its files, its command or the install changed, or an export is
  missing; a rebuild with nothing changed starts no JavaScript process. The folders of the packages become frontend
  inputs of the application, also for Visual Studio's fast up-to-date check, so the application's own
  `RunicApplicationFrontendInput` items for them are no longer needed.
- **As data.** `RunicViewsGetFrontendDependencies` returns an application's frontend packages, with their owner, kind,
  build and watch commands and mapped specifier, without building; `obj/runic/frontend-graph.json`
  (`rax.frontend-graph/1`) lists the JavaScript side.

See [Library packages](../../../packages/dotnet/Runic.Application.Views/README.md#library-packages).
