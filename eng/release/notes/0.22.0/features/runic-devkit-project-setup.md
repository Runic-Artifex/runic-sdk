## Runic DevKit: the RunicApplication capability and the template's `.vsconfig`

The Runic Views build targets declare the `RunicApplication` project
capability in every project that references Runic Views: the window project
and its ViewModel libraries and tests. Visual Studio loads Runic DevKit only
for a solution with such a project (`SolutionHasProjectCapability:RunicApplication`).

New `runic-app` projects have a `.vsconfig` that names the Visual Studio
components their launch profiles need: the .NET desktop development workload
(`Microsoft.VisualStudio.Workload.ManagedDesktop`) and JavaScript diagnostics
(`Microsoft.VisualStudio.Component.JavaScript.Diagnostics`), an optional
component of that workload that Visual Studio's JavaScript debugger needs for
WebView2 script debugging and for the page in Chrome or Edge. Visual Studio
offers to install a missing one when it opens the project. To add it to an
existing project, copy the file from a new one into the directory of its solution.

The Runic extension for VS Code is now Runic DevKit, with the ID
`runic-artifex.runic-devkit` (it was `runic-artifex.runic`); `docs/debugging.md`
and the template's `.vscode/extensions.json` name it.
