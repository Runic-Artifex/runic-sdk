# Runic SDK 0.21.0

Install matching `0.21.0` packages from NuGet and npm;
`Runic.Application.Views.CsWebUi` is `0.21.0-preview.1`.
Tested with Runic Translations 0.13.0 and Runic Command Line 0.13.0.

0.21.0 brings your IDE's JavaScript debugger to the page on every operating
system. On Linux, `dotnet runic dev` serves the Window's WebKitGTK page to
JavaScript debuggers through a Chrome DevTools Protocol adapter, so VS Code
stops at breakpoints in the frontend's sources as it does with WebView2 on
Windows (#1029, preview). `dotnet runic dev --browser` serves the Window to
Chrome or Edge instead, so the page's JavaScript can be debugged in the IDE on
macOS too (#1053).

The browser's own developer tools learn about Runic: React DevTools labels the
hooks' values and Vue DevTools gets a **Runic Views** inspector (#1041), the
console shows View clients and states readably (#1042), WebView2 windows open
their DevTools window from code and load DevTools extensions in Development
(#1056), and a **Runic** panel for Chrome's and Edge's developer tools ships
with `dotnet-runic` (#1060). All of it is development-only.

Hosted application windows (experimental) give a GTK 4 window back to the user
after a menu-bar choice (#1045) and treat a Linux guest that holds capabilities
as elevated (#1047). The development endpoints are hardened: the Vite plugin's
`/__runic/*` endpoints and HMR events answer only admitted clients (#1052), and
WebView2's debugging endpoint opens only in Development (#1071), as WebKit's
remote inspector did in 0.20.

0.21.0 also carries the API review before 1.0: .NET members added since 0.12
are renamed so that one feature uses one word, trusted component mode moves to
the npm packages' `./experimental` entries, two development formats take dotted
names, and internal MSBuild names start with `_Runic`. The 0.21 release train is
Runic Translations 0.13.0, runic-sdk 0.21.0 and the Runic editor extensions
0.21.0; Runic Command Line stays at 0.13.0. These changes need edits in an
application: [Upgrading from 0.20](#upgrading-from-020) lists them, and the
[migration guide](https://github.com/Runic-Artifex/runic-sdk/blob/v0.21.0/docs/migration-1.0.md#from-020-to-021)
collects them.
