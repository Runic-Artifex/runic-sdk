# Provisional View Bridge managed-watch policy

`dotnet runic dev` passes `--no-hot-reload` to `dotnet watch` only when the
project declares `RunicApplicationViewBridgeReadyManifest`. The generated
View Bridge contract is inspected from a compiled assembly after MVVM source
generators run. A .NET Hot Reload patch can otherwise change an ordinary
ViewModel property in the running host while leaving the IR, TypeScript
contract, route adapter, and ready fingerprint at their previous shape.

For this opt-in mode, each managed source edit builds and restarts through
`dotnet watch`; the replacement host acknowledges the fingerprint it loaded.
There is no second manifest-triggered host restart owner. Ordinary projects
keep their existing .NET Hot Reload behavior. Vite frontend module HMR remains
live in both modes.

The focused `runic-dev-view-bridge.browser.test.mjs` probe changes a compiled
fixture contract. It requires the ready fingerprint to change, one replacement
managed process to acknowledge that value, and one Vite browser replacement
after acknowledgement. This guard favors contract correctness over body-only
.NET Hot Reload in View Bridge projects. It does not establish Visual Studio
F5 behavior or native-window document replacement after a managed restart.
