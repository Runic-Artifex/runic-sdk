# Web bundle

A web bundle (`rax.web-bundle/1`) carries the built frontends of a build's projects to a build on another machine,
with what each was built from. A CI job on Linux, which has Node.js or Bun, builds the frontends and writes the bundle;
the job that publishes for the target OS, such as Windows, takes the pages from it (D082). Each entry records the
[fingerprints](contract-ir.md#fingerprint) of the Views its page was built from, so that the publishing build can
compare them with the contract IR that its own generator writes.

The bundle is a **build artifact**: CI uploads and downloads it between the jobs of one commit. It is not a package and
not a cache key. The Views targets write it
([Building the frontend in another job](../../packages/dotnet/Runic.Application.Views/README.md#building-the-frontend-in-another-job)):

| Property | Effect |
| --- | --- |
| `RunicApplicationFrontendBundleDirectory=<dir>` | After its build, every project that declares Views writes its entry into `<dir>` |

| File | Content |
| --- | --- |
| This document | The layout and the manifest |
| [`web-bundle/web-bundle.schema.json`](web-bundle/web-bundle.schema.json) | The JSON Schema (draft 2020-12) of `web-bundle.json` |

The key words MUST, MUST NOT, SHOULD and MAY are used as in RFC 2119.

## Layout

```
<bundle>/
  web-bundle.json              the manifest, format rax.web-bundle/1
  projects/
    Acme.App/
      www/                     the frontend build's output (dist/), as build and publish copy it to www/
      clients/                 the generated clients (src/generated), for a job that tests the frontend alone
    Acme.App.json              the project's fragment: its entry of web-bundle.json
    Acme.Shared/
      clients/                 a client library: clients only, no page
    Acme.Shared.json
```

One folder and one fragment per project, named by its **assembly name**, so that the projects of a parallel solution
build write their entries side by side. Each project replaces its own folder and fragment, and then rewrites
`web-bundle.json` from every fragment present, under a lock. A bundle directory SHOULD be empty before the build that
writes it, or written by builds of the same solution; a fragment that an earlier build left there stays in the manifest.

`www/` holds the page's files only. The host's Views client (`runic-views.js`) is not in it: the publishing build
copies its own, of its own Runic release, as for a page it built itself.

## Manifest

`web-bundle.json` is one JSON object (UTF-8, no byte order mark):

| Member | Content |
| --- | --- |
| `format` | `rax.web-bundle/1` |
| `producer` | `{name, version}` of the build integration that wrote it, such as `Runic.Application.Views` `0.24.0` |
| `createdBy` | `os` (the runtime identifier of the writing machine, `linux-x64`), `dotnet` (the .NET SDK version) and `node` (the Node.js version on `PATH`, when there is one) |
| `source` | The Git `commit` of the checkout and whether tracked files differed from it (`dirty`); absent outside a Git checkout |
| `projects` | The entries, sorted by assembly name |

An entry:

| Member | Content |
| --- | --- |
| `assembly` | The assembly name. A reader finds a project's entry by it. |
| `project` | The project file, relative to the checkout's root (the file name outside a checkout) |
| `kind` | `application`: an executable with a page. `library`: a model project whose page an application copies, as beside a WPF application. `clientLibrary`: a package that applications' pages bundle ([client libraries](../../packages/dotnet/Runic.Application.Views/README.md#share-viewmodels-and-their-clients-through-a-library)); it has no page. |
| `configuration`, `targetFramework` | The build's |
| `host` | `RunicApplicationHost`, which the frontend build received as `RUNIC_APPLICATION_HOST`; absent when none |
| `nodeEnv` | The `NODE_ENV` the frontend build received (`production` in an optimized build); absent when unset |
| `www` | `projects/<assembly>/www`, except for a client library |
| `clients` | `projects/<assembly>/clients`, when the emitter wrote clients |
| `contracts` | `unit` (the contract IR's producer unit), `fingerprintFormat` (`runic.views.fingerprint/1`), `irSha256` (the SHA-256 of the IR document) and `views`: every View's `{id, fingerprint}` from the IR, sorted by `id` |
| `frontend` | The frontend `directory` (relative, as `project`), its `packageManager`, the `lockFile` and its `lockSha256` at the install root (the JavaScript workspace's, or the frontend's), the `runtimePackage` `{name: "@runic-artifex/views", version}` that the frontend resolved from its `node_modules`, and the `buildCommand` |
| `files` | `{count, sha256}` of `www/`: `sha256` is the SHA-256 of the lines `<sha256>  <path>\n` (the output of `sha256sum`) of every file, its path relative to `www/` with `/`, sorted by path in ordinal order |

Paths are relative and use `/`. Hashes are lowercase hexadecimal SHA-256, except the View fingerprints, which keep the
IR's uppercase form.

A reader MUST reject a manifest whose `format` it does not know and SHOULD ignore members it does not know. A member
that an existing reader would misread, or a new required member, is a new format version, `rax.web-bundle/2`; optional
members are added in a new revision of the schema. The format and file names carry no `runic` name, so a producer in
another language, such as a `rax build` of a Rust or C++ application, writes and reads the same bundle.
