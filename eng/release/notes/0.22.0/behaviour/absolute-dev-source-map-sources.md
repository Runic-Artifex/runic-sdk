- **`vite dev` source maps name absolute files.** `runic()` makes the `sources` of the
  source maps that the development server sends with each module absolute file paths
  (`/home/me/App/Frontend/src/App.tsx`, `C:/Users/me/App/Frontend/src/App.tsx`) instead of
  Vite 8's paths relative to the module. Visual Studio's JavaScript debugger, which attaches
  to the page with no web root, then binds breakpoints in `Frontend/src`; VS Code's
  JavaScript debugger, browser developer tools and Runic Inspect read them as before.
  `vite build` and `vite preview` output is unchanged. The Angular CLI's development server
  keeps its own maps. See
  [The development server's source maps](https://github.com/Runic-Artifex/runic-sdk/blob/v0.22.0/docs/debugging.md#the-development-servers-source-maps).
