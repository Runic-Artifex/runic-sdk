# Managed View Bridge restart ownership probe

Run from the repository's locked shell:

```sh
direnv exec . tests/fixtures/application/DevRestartOwnership/probe.sh
```

With the workspace's declared Bun dependencies available, run the Vite and
headless Chromium extension:

```sh
direnv exec . bun run --cwd packages/web/vite-plugin-runic build
direnv exec . bun test --timeout 120000 packages/web/vite-plugin-runic/test/view-bridge-watch.browser.test.mjs
```

The executable probe copies this minimal project to a disposable directory,
builds it, and starts `dotnet watch run` with the watch arguments used by
`HostProcessController`. The MSBuild `PublishContractReady` target publishes
`obj/Debug/net10.0/view-bridge.ready.json` after each build. The contract source
is also embedded in the assembly. At startup, the host hashes the embedded
bytes, records its process ID, and atomically writes the fingerprint it
actually loaded to `RUNIC_VIEW_BRIDGE_HOST_READY`.

The probe renames the contract type, waits for the ready manifest to change,
and checks that exactly one replacement host started and acknowledged the new
fingerprint. It leaves the checked-in fixture unchanged and removes the
disposable project and logs after printing the result. A successful run prints
`DEV_RESTART_OWNERSHIP_OK` and the relevant `dotnet watch` output.

On 2026-09-25, the locked .NET 10.0.400 shell reported two total host starts:
the initial process and one replacement. `dotnet watch` reported that the
contract shape edit needed a restart, and the replacement's fingerprint
matched the newly published manifest. This supports retaining `dotnet watch`
as the sole managed restart owner for this edit. The dev tool must not add an
independent manifest watcher that restarts the host too.

The shell probe exercises the managed watcher, MSBuild publication, and host
acknowledgment only. It does not launch Vite. One measured
shape edit does not establish a universal one-restart guarantee across all
contract edits or hosts.

The browser extension starts an actual Vite server with the passive View
Bridge plugin and a headless Chromium page. A frontend module edit updates
the page through HMR without another document load or host start. It then
changes the managed contract type. The replacement host deliberately waits
at a test-only acknowledgment gate, letting the test verify that Vite has
seen the new manifest but has sent no full reload. Releasing the gate lets
the replacement write its loaded fingerprint; Vite then sends one guarded
full reload and Chromium opens the second document. The locked shell emitted
`DEV_VITE_VIEW_BRIDGE_WATCH_OK` on 2026-09-25.

The browser extension uses the real passive plugin and Vite server, but it
still does not exercise `dotnet runic dev`, a native window, or a generated
adapter. The source-byte fingerprint is a deliberately simple fixture stand-in
for the future compiled contract fingerprint. A complete integration test
becomes possible when the generated adapter can acknowledge the exact compiled
fingerprint in a running application.

`packages/web/vite-plugin-runic/test/runic-dev-view-bridge.browser.test.mjs`
adds the CLI boundary. It copies this fixture, runs the actual `dotnet runic
dev` command from the source tool, and lets that command launch the fixture's
Vite configuration and `dotnet watch` host. It verifies a local Vite HMR edit,
one contract-shape restart, the replacement host's loaded fingerprint, and one
browser reload after that acknowledgment. The fixture's `Frontend/run-vite.mjs`
only locates the workspace Vite binary for the test; an application would use
its ordinary package dependency. This remains an orchestration proof: there is
still no generated View Bridge runtime adapter or native-window integration.

Run that focused browser probe after building the workspace packages:

```sh
direnv exec . bun test --timeout 150000 packages/web/vite-plugin-runic/test/runic-dev-view-bridge.browser.test.mjs
```
