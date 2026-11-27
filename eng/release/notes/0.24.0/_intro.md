# Runic SDK 0.24.0

Install matching `0.24.0` packages from NuGet and npm;
`Runic.Application.Views.CsWebUi` is `0.24.0-preview.1` and
`Runic.Spelling` is `0.24.0-preview.1`.
Tested with Runic Translations 0.16.0 and Runic Command Line 0.15.0.

0.24.0 makes an application with more than one project work from a fresh
clone, lets a CI build the web on Linux and publish on Windows or macOS without
Node.js, makes `dotnet runic dev` and the IDEs start an application the same
way, and states the Views, host, server and assets contracts so that a Rust or
C++ implementation can be written and checked from the specification alone.

A library of ViewModels now generates their TypeScript clients once, into its
own frontend package, and the applications that present them import them from
there (#1318). The build installs a Bun, npm or pnpm workspace once at its
root, builds the library and workspace packages that the page imports, and
checks their exports; `dotnet runic dev` watches those packages and rebuilds
them, and `dotnet runic doctor` checks the workspace, the package managers and
how the page's imports resolve. `dev`, `doctor`, `package` and `size` start
from a solution root, and `dotnet new runic-app --library <Name>` creates an
application and a client library in one solution.

For split CI, a build writes a web bundle (`rax.web-bundle/1`) with every
project's page, generated clients and View fingerprints; a build on another
machine publishes from it with neither Node.js nor Bun, and fails when its own
Views no longer match the bundle's. `dotnet runic bundle` writes and checks a
bundle, `dotnet runic package --frontend-bundle` publishes from one, and
[`examples/split-ci`](https://github.com/Runic-Artifex/runic-sdk/tree/v0.24.0/examples/split-ci)
shows the workflow for GitHub, Gitea and Forgejo Actions.

`dotnet runic dev` now runs the Window with the project's first launch profile,
as `dotnet run` and the IDEs do, passes both the project's `StartArguments` and
the profile's arguments as Visual Studio and Runic DevKit do, and `doctor`'s
`launch-arguments` check shows what each one passes. For application code, a
field can write only when it loses focus (#1316), a View makes command handles
on demand (#1317), an interaction can be answered from the component's own
template (#1319), a ViewModel can publish only the members marked
`[RunicInclude]` (#1320), and a hosted region tells a slow guest from a failed
one (#1377, #1380, experimental).

The Views TypeScript clients are now written from the contract IR by
`@runic-artifex/views/codegen`, which the build runs with Node.js or Bun, and
each View has a contract fingerprint of its own, so every generated contract
string changes once. The Views conformance corpus, the Runic host protocol
scenarios, a new Desktop server corpus and the Assets archive vectors run
through one language-neutral driver, against the .NET implementation, the
native Desktop server and the TypeScript client. The native Desktop server is
now `rax_server` (C ABI 6), and it, the Kestrel server and the Assets readers
follow the RFCs and the archive specification where they did not.

0.24.0 fixes the Views runtime's ordering and capacity edge cases, a client
that did not type-check for a field named like a descriptor member, CS-WebUI's
Development browser left running after a hard kill, Linux browser profiles and
directory watches, and `dev`'s stops and restarts. See [Fixes](#fixes).

The 0.24 release train is Runic Command Line 0.15.0, Runic Translations 0.16.0
and runic-sdk 0.24.0; Runic's editor extensions 0.24.0 follow this release.
[Upgrading from 0.23](#upgrading-from-023) lists what an application changes,
and the
[migration guide](https://github.com/Runic-Artifex/runic-sdk/blob/v0.24.0/docs/migration-1.0.md#from-023-to-024)
collects it.
