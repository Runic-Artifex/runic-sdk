- **An application whose library package is compiled builds from a fresh clone.** Nothing built a client library's
  frontend package, so the application's bundler failed with `Failed to resolve import "@acme/shared-ui"`, reported as a
  generic `RUNICBRIDGE016`. The library now builds its package, and a package that a frontend depends on and that has
  not been built is `RUNICBRIDGE088`, which names the package and the project that builds it. A frontend build that
  still fails on such an import names the package in `RUNICBRIDGE016`.
- **A compiled library package keeps its client types.** `tsc` does not copy the generated `.contract.d.ts` files, so a
  consumer's type check failed with `TS7006` or saw `any`. The build copies them beside the compiled clients.
