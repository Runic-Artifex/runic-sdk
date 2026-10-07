# GTK4 sandbox fixture

This is a disposable Linux desktop test application, not a production packaging recipe.
Use the [container runner](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/desktop/container-automation.md)
for runtime preparation, installation and execution.
It uses a standard GNOME Platform runtime so both the application sandbox and
WebKit's nested process sandbox work without exposing `/nix/store` or the home
directory. Network permission serves Runic's localhost bridge; the nested WebKit
process is launched with its own `--no-network` sandbox.

From the SDK root, build with the locked development environment:

```sh
direnv exec . bash tests/native/Runic.Desktop.Gtk4.Smoke/flatpak/publish.sh 10.0.11
```

The script retains the locked SDK/compiler and downloads the matching Microsoft
NativeAOT target runtime pack through the repository's NuGet configuration/cache.
The Nix target runtime embeds Nix ICU/OpenSSL paths that are unavailable in the
standard Flatpak runtime. The portable target pack avoids those paths without
turning off globalization. Only the disposable test executable gets its ELF
interpreter/runpath adjusted. Output: `artifacts/gtk4-flatpak/Runic.Desktop.Gtk4.Smoke`.

The maintained [container runner](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/desktop/container-automation.md)
prepares the pinned standard runtime (`org.gnome.Platform/x86_64/50`) outside the
isolated desktop. The verified runtime commit is
`545da92354a265d2c3572c91c39ac14dd7e74f9d8f9b66744ad50f478d2497c5`.
Copy `install.sh` and the portable executable into the runner's frozen test
inputs; the runner installs the fixture inside each fresh container.

The installer overwrites only three named test inputs in
`~/runic-sandbox-inputs`: `granted.txt`, `private.txt`, and `save-target.txt`.
The fixture must deny direct access to `private.txt`, open `granted.txt` through
the portal, and reject atomic replacement of `save-target.txt` without changing
it. Also check cancellation and closing the owner during a pending picker.
Use the [container runner](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/desktop/container-automation.md)
for GNOME or KDE automation. The installer accepts an optional second argument,
`wayland` (default) or `x11`, and grants only that display socket. The managed
KDE `--backend x11` mode supplies the XIM environment for real Pinyin input. Native accessibility is checked outside the application
sandbox against the actual exported tree.

The installer builds a local Flatpak repository at `~/.cache/runic-gtk4-flatpak/repo`
inside the test desktop. The container discards it, the installed application and the
sandbox input files at shutdown; retain the copied results and logs you need.
