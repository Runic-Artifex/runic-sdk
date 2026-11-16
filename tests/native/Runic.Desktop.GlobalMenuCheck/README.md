# Global menu check (Linux, manual)

A manual check of the Linux global menu on a real KDE Plasma session: the Global Menu applet, or the title bar's
application menu button. CI's `GlobalMenuSmoke` and `GlobalMenuSessionSmoke` (in `Runic.Desktop.Gtk4.Smoke`) run
under Xvfb against a fake menu registrar. They check what Runic exports over D-Bus. This app checks what a person
sees: whether Plasma renders the menu, the menu follows focus between windows, the bar returns to the window when
the applet goes away, and shortcuts, access keys, check marks and unusual titles look right.

The app uses `Runic.Desktop` directly, with no frontend build. Each window's page shows the current
`MenuBarPlacement`, a log of every command run from the menu, and a checklist you tick as you go. The console prints
the same log, including Runic's events 3015 (placement changed) and 3016 (global menu failed).

## Run it

Run it in a Plasma session with the Global Menu applet on a panel, or with the application menu button in the title
bar:

```sh
direnv exec . dotnet run --project tests/native/Runic.Desktop.GlobalMenuCheck -- [options]
```

| Option | Effect |
| --- | --- |
| `--gtk4` | GTK 4 / WebKitGTK 6 instead of GTK 3 / WebKitGTK 4.1 |
| `--windows N` | Open N windows at start (1–8), to check that the menu follows focus |
| `--frameless` | Frameless windows: no in-window bar, but the global menu still works |
| `--no-global-menu` | `LinuxDesktopOptions.GlobalMenu = false`: the bar must stay in the window |

Check both backends: one run without options, one with `--gtk4`. Also do one run each with `--windows 2`,
`--frameless` and `--no-global-menu`.

## Expected result

- With the applet present, the placement badge says **Global**, the in-window bar is hidden, and the console logs
  event 3015 but not 3016. The applet shows File, Edit, View, Tools (rev N) and Help, with Help last.
- Every item on the page's checklist holds:
  - Commands log the focused window's number.
  - Shortcut text is shown.
  - Submenus open, three levels deep.
  - Disabled items and submenus are disabled, and `CanExecute` toggles Cut, Copy and Paste.
  - Check and radio marks follow the state, including View ▸ Live Check, which changes only through `IsChecked`.
  - Rebuilding the menu bar updates the Tools title and its added item live.
  - The Extra menu appears before Help and goes away again.
  - Unicode, long and `_`/`&` titles render literally.
- When you remove the applet, the placement changes to **Window** and the in-window bar appears, where Alt+F opens
  File and access keys are underlined. When you add the applet back, the placement returns to **Global**.
- With `--frameless`, the placement is **Global** with the applet and **None** without it. With `--no-global-menu`,
  it is always **Window**.

Without a menu registrar, for example under Xvfb, the app starts and reports placement **Window** (**None** when
frameless). That only shows that it runs; it checks nothing about the global menu.
