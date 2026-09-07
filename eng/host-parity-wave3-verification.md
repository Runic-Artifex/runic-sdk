# Hosting and footprint implementation verification — 2026-09-07

Verified through the repository Nix development shell on Linux x64:

- Complete Release `bun run verify` passed, including managed and frontend tests,
  generated contract checks, package builds and cold package canaries.
- Both default and minimal Desktop profiles passed all 69 conformance tests.
  Chromium's redirected diagnostics are now drained and retained; configured
  Playwright Chromium is preferred over an unrelated system browser.
- CLI unit tests: 24 passed. The size command's end-to-end failure test passed:
  a failing checker is recorded, an existing report remains unchanged, and a
  failed publish retains a report without running the checker.
- Three fresh NativeAOT customer applications consumed only NuGet SDK candidates
  and a shared frontend built from npm package archives. Default Desktop, minimal
  Desktop and CS-WebUI passed identical browser acceptance and five fresh-process
  lifecycle cases each. No new system core dumps were observed.
- The Release template-artifact checker passed after correcting its hard-coded
  Debug inspector path. The workflow parses with the repository's YAML library.

The initial matrix used a workspace-built frontend. The final package-only run
also exposed and fixed the customer Vite config's undeclared Node-types dependency.
Final receipts will use a committed revision and include package hashes.

The expanded CI matrix is configured for Linux x64, Windows x64, macOS x64 and
macOS arm64. These local checks certify Linux x64 only. Earlier CI also exposed
browser startup instability and the stale inspector path; native macOS CI was
still running when this wave was prepared. Do not interpret workflow configuration
as completed platform validation.
