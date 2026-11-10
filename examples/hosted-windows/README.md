# Hosted windows

A shell application shows another Runic application's windows as its own
([hosted applications](../../packages/dotnet/Runic.Application.Views.Desktop/README.md#hosted-applications)).
The counter is a separate executable with its own process, ViewModels and
translations. The shell starts it and opens a native window for each of its
Windows. That window shows the counter's page, its menu bar and its dialogs. The
counter's windows follow the shell's language and theme, and the shell's policy
decides what the counter may do. Hosted applications are experimental: both
projects suppress
[`RUNICDESK103`](../../docs/experimental.md#RUNICDESK103).

| File | What it does |
| --- | --- |
| [`Counter/Program.cs`](Counter/Program.cs) | The guest. It opts in to hosted mode only with its own `hosted` parameter (`RunicDesktopHostOptions.Guest`). Without the parameter it opens its own window. |
| [`Counter/CounterServices.cs`](Counter/CounterServices.cs) | The menu bar, set again in the application's language after each switch. The message boxes and file launcher come from `AddRunicPlatformServices`, and `AddRunicWindowService` provides the language that follows the host's. |
| [`Counter/CounterViewModel.cs`](Counter/CounterViewModel.cs) | Count, Reset… with a message box, and a link to the documentation. It does not know whether it is hosted. |
| [`Counter/Tests`](Counter/Tests/HostedCounterTests.cs) | The counter in hosted mode without a shell: `RunicTestHost` plays the host and checks the menu bar, the language, the message box and the link. |
| [`Shell/ShellViewModel.cs`](Shell/ShellViewModel.cs) | Starts the counter with `IRunicHostedApplications.StartAsync(new RunicGuestStartInfo(path, ["hosted"]), …)` and watches it exit. It also switches the language (`IRunicApplicationLocale`) and the theme. |
| [`Shell/CounterPolicy.cs`](Shell/CounterPolicy.cs) | The shell's `IRunicHostedWindowPolicy`. It puts the counter's name in front of each dialog title and opens only links to the Runic documentation. |
| [`Shell/Program.cs`](Shell/Program.cs) | Selects an embedded web view on Linux, because hosted windows have no browser fallback. It applies the theme to every window of the shell, including the counter's. |

Run the shell. It builds the counter first:

```sh
dotnet run --project examples/hosted-windows/Shell -c Release
```

Choose **Open the counter**. The counter's window opens with its **Counter**
and **Help** menus. Switch the shell to Deutsch: the counter's menus and page
switch with it. Switch the theme: the counter's page follows. **Reset…** shows
the counter's message box, titled `Counter: …` by the shell's policy, over the
counter's window. **Runic documentation** opens the documentation in the
browser. A link to any other site would be refused, and the counter would show
that on its page. Closing the counter's window ends the counter. Closing the
shell's window ends both.

## What each side does

- **Opt-in.** The counter decides whether it can be hosted. Only its `hosted`
  parameter reaches `RunicGuestOptions.FromStandardInput()`, which reads the
  host's offer from standard input. Runic defines no flag or environment
  variable, so an application without such a code path cannot be hosted, and
  the processes it starts do not inherit the mode. Started with `hosted` from a
  terminal, the counter fails with `RunicHostedApplicationException` and exits.
- **Windows.** In hosted mode the counter opens no window and runs no toolkit
  loop. Each `OpenWindowAsync` asks the shell, which opens a native window that
  loads the counter's page from the counter's own loopback address. That window
  has a data store of its own and none of the shell's credentials.
- **Menus and dialogs.** The counter's menu bar shows in its windows, and an
  item chosen there runs the counter's command. The counter's message box and
  file launcher go to the shell, which shows them owned by the counter's window.
- **Language and theme.** The counter's `IRunicApplicationLocale` follows the
  shell's. Its catalog (`Counter/Frontend/translations`), its menu bar and its
  pages follow too. A language the catalog lacks falls back to English. The
  shell's `DesktopHost.SetColorSchemeAsync` applies to the counter's windows as
  they are native windows of the shell.
- **Policy.** Without a policy, every window, menu and dialog of a guest is
  shown unchanged, and window services follow conservative defaults. The
  clipboard is read and written only for the window the user works in, and only
  web and mail links and files the user gave the guest are opened. `CounterPolicy` marks the dialogs
  and narrows the launcher further.
- **End.** When the counter exits or crashes, the shell closes its windows and
  `RunicHostedApplication.Exited` completes. When the shell exits, the counter
  sees its standard input end and exits too.

## Checks

```sh
dotnet run --project examples/hosted-windows/Counter/Tests -c Release
xvfb-run -a dotnet run --project examples/hosted-windows/Shell -c Release -- --probe
```

The counter's tests start the built counter with `RunicTestHost` and need no
display. `--probe` is CI's check on a real desktop. The shell starts the
counter, waits for its window and menu bar, switches to German and stops it.
CI runs both in the Window and View journeys job.

The example builds the Runic packages from source. To copy it, replace the
`ProjectReference` and `Import` lines with package references, as
[First Window](../first-window/README.md#copy-it-into-your-own-project) shows.
Replace the `CounterTool` path in `HostedShell.csproj` with wherever your shell
installs its tools.
