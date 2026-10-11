# First Window on Runic Desktop

The counter from [First Window](../first-window/README.md) hosted by Runic
Desktop instead of CS-WebUI, with a ReactiveUI ViewModel. Use this host when an
application needs a native window, an embedded WebView, or the platform
services described in the
[host selection guide](https://docs.runic-artifex.eu/guides/desktop/host-selection/).

| File | What it does |
| --- | --- |
| [`CounterViewModel.cs`](CounterViewModel.cs) | A ReactiveUI ViewModel with a count and an increment command. |
| [`CounterWindow.cs`](CounterWindow.cs) | The partial Window that selects the ViewModel. The same declaration opens on CS-WebUI. |
| [`Program.cs`](Program.cs) | Runs `RunicDesktopHost.Run`, opens the Window with `OpenWindowAsync<CounterWindow>()`, and awaits `WaitForCloseAsync()`. |
| [`Frontend/src/app.ts`](Frontend/src/app.ts) | Uses the same generated client contract as the CS-WebUI examples. |

The Desktop surface serves `webui.js`, and `Runic.Application.Desktop` copies
`runic-desktop-views.js` into `www`; `Frontend/index.html` loads both before
the generated client. The
[Runic.Desktop guide](../../packages/dotnet/Runic.Desktop/README.md) lists the
native prerequisites for each platform and the embedded WebView choices.

```sh
dotnet run --project examples/first-window-desktop -c Release
```

`--serve-only` starts the authenticated surface without opening a window, so
an external browser test can drive the generated client; `--probe-owner`
checks that closing the Window releases its ViewModel scope. CI runs both. The
example builds the Runic packages from source; replace its `ProjectReference`
and `Import` lines with `Runic.Application.Desktop` and
`Runic.Application.ReactiveUI` package references to copy it out.
