# Runic SDK 0.22.0

Install matching `0.22.0` packages from NuGet and npm;
`Runic.Application.Views.CsWebUi` is `0.22.0-preview.1`.
Tested with Runic Translations 0.14.0 and Runic Command Line 0.13.0.

0.22.0 makes published applications smaller without taking anything away. A
NativeAOT size pass measured what Runic adds to an executable and cut it on
every host: the size fixture's Runic Desktop host on linux-x64 goes from
14.56 MB to 10.61 MB, and its CS-WebUI application from 10.71 MB to 7.47 MB,
now that the CS-WebUI window serves blobs from Runic's own loopback server
instead of `HttpListener`. Each bridged ViewModel costs 16.7 KB instead of
0.21's 56.3 KB (#1134, #1138, #1144): generated Bridges share their codecs and
command runners per assembly and describe their members as data. Generated
TypeScript and the wire protocol stay byte for byte the same. Views and members
that only development needs can be marked `[RunicDevelopmentOnly]`, and a
trimmed or NativeAOT publish then leaves them out (#1109).

Framework-dependent publishes get smaller too. A RID-specific publish of a Runic
Desktop application ships only its own operating system's platform assemblies
(1.9 to 3.4 MB less, 4.1 MB with ReadyToRun), with an opt-out for code that
needs the others. Single-file publish of a Views application now works, and
the size budgets now gate the framework-dependent publish too. The repository's
own publishes leave out the Bridge generator's launcher and the XML docs, so the
budgets measure what an application gets (FD5). The host-named copies of the
Views client script, `runic-desktop-views.js` and `runic-cswebui.js`, are
deprecated: they log a console warning in 0.22, and 0.23 removes them.

Visual Studio debugs a Runic application end to end through Runic DevKit, the
Runic editor extensions for Visual Studio and VS Code, which share that name
from 0.22. Debug the Window starts the Window under the .NET debugger and
attaches the JavaScript debugger to the page. The development server's source
maps name absolute files, so breakpoints in `Frontend/src` bind. F5 builds after
a frontend-only edit, and an IDE can stop `dotnet runic dev` gracefully.
CS-WebUI pages open a Chrome DevTools Protocol port for IDE debuggers in
Development. On Linux, VS Code's js-debug no longer leaves the WebKitGTK page
stopped after a reload or at a breakpoint it never set (#1108, #1139), and
`dev` names whatever holds port 9222 (#1065). The side panel and the close drain
are stable.

Runic Translations 0.14.0 was published on its own ahead of the train, and
runic-sdk adopts it in 0.22. The 0.22 release train is runic-sdk 0.22.0 and the
Runic editor extensions 0.22.0, with Translations 0.14.0; Runic Command Line
stays at 0.13.0. [Upgrading from 0.21](#upgrading-from-021) lists what an
application changes, and the
[migration guide](https://github.com/Runic-Artifex/runic-sdk/blob/v0.22.0/docs/migration-1.0.md#from-021-to-022)
collects it.
