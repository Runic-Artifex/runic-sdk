# ReactiveUI first window on Runic Desktop

This sample uses the same generated Views client contract as the CS-WebUI
examples, hosted by `Runic.Desktop` through `Runic.Application.Desktop`.

```sh
direnv exec . dotnet run --project examples/first-window-desktop -c Release
```

`--serve-only` starts the authenticated surface without launching a browser, so
the generated client can be exercised by an external browser test.
