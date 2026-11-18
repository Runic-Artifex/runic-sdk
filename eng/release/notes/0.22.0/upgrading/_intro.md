## Upgrading from 0.21

Update every Runic NuGet and npm package to 0.22.0 together and rebuild, so that
the generated clients and Bridges are regenerated. The Views protocol and the
generated TypeScript are unchanged, so a 0.21 page and a 0.22 host still connect,
and Bridges that 0.21's generator wrote keep working with the 0.22 runtime.

Most applications change nothing. A page that loads `runic-desktop-views.js` or
`runic-cswebui.js` loads `runic-views.js` instead, and code that imports
`onBridgeDraining` from `@runic-artifex/views/experimental` imports it from the
root entry. Both old forms still work in 0.22 and are removed in 0.23. A Runic
Desktop application whose own code calls another operating system's
`Runic.Platform` API reads
[the RID-specific publish change](#behaviour-and-output-changes). An application
that uses Runic Translations moves to 0.14.0. Update Runic's editor extensions to
0.22.0 too: Runic DevKit for Visual Studio debugs the Window, and the VS Code
extension's ID is now `runic-artifex.runic-devkit`.

