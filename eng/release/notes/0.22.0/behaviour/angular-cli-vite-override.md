- **Angular CLI starter dependencies.** The Angular starter (the Angular CLI
  build) and the Notes Angular examples move `@angular/build` and `@angular/cli`
  to 22.2.7 and no longer override `vite`: `@angular/build` now depends on Vite
  8.3.5, which has the GHSA-587h-7wmj-h5pm fix. A pnpm project created with the
  Angular CLI build no longer gets a `pnpm-workspace.yaml`; `--angular-build vite`
  projects are unchanged. An existing Angular CLI project can remove its `vite`
  override (in `package.json`, and for pnpm in `pnpm-workspace.yaml`) after moving
  to `@angular/build` 22.2.5 or later.
