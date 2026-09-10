# GTK4 sandbox fixture

This is a disposable Linux desktop test application, not a production packaging recipe.
Use the [container runner](../../../../docs/guides/desktop/container-automation.md)
for automated runtime preparation and execution. The manual guest steps below
remain available for the legacy VM helpers.
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

Inside the disposable portal VM, install the standard runtime:

```sh
flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
flatpak install --user --assumeyes flathub org.gnome.Platform/x86_64/50
```

The verified runtime commit is
`545da92354a265d2c3572c91c39ac14dd7e74f9d8f9b66744ad50f478d2497c5`.
For a matching repeat, use `flatpak update --user --commit=<commit>
org.gnome.Platform/x86_64/50` and verify `flatpak info --user` before testing.
Copy the portable executable through the VM exchange directory, then run:

```sh
bash /home/runic/src/tests/native/Runic.Desktop.Gtk4.Smoke/flatpak/install.sh \
  /tmp/xchg/Runic.Desktop.Gtk4.Smoke
flatpak run --user com.runic.tests.Sandbox
```

The installer overwrites only three named test inputs in
`~/runic-sandbox-inputs`: `granted.txt`, `private.txt`, and `save-target.txt`.
The fixture must deny direct access to `private.txt`, open `granted.txt` through
the portal, and reject atomic replacement of `save-target.txt` without changing
it. Also check cancellation and closing the owner during a pending picker.
Use the [automated VM runner](../../../../docs/guides/desktop/vm-automation.md)
for the GNOME sequence. Native accessibility is checked outside the application
sandbox against the actual exported tree.

The local Flatpak repository is retained at `~/.cache/runic-gtk4-flatpak/repo` to
reuse installation work. After testing, uninstall this test application with
`flatpak uninstall --user com.runic.tests.Sandbox`; retain useful logs and the VM
state if more checks are pending. Do not prune unrelated Flatpak/Nix caches.
