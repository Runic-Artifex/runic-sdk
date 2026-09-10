#!/usr/bin/env bash
# Run only in a disposable graphical test guest with GNOME Platform 50 installed.
set -euo pipefail
binary=$(realpath "${1:?Pass the portable test executable}")
root="$HOME/.cache/runic-gtk4-flatpak"
mkdir -p "$root" "$HOME/runic-sandbox-inputs"
app=$(mktemp -d "$root/build.XXXXXX")
trap 'rm -rf "$app"' EXIT
mkdir -p "$app/files/bin" "$app/files/lib/runic" "$app/files/share/applications"
cp "$binary" "$app/files/lib/runic/Runic.Desktop.Gtk4.Smoke"
chmod +x "$app/files/lib/runic/Runic.Desktop.Gtk4.Smoke"
printf '%s\n' 'Runic granted sandbox document.' > "$HOME/runic-sandbox-inputs/granted.txt"
printf '%s\n' 'Runic private sibling; must not be readable.' > "$HOME/runic-sandbox-inputs/private.txt"
printf '%s\n' 'Preserve this existing save destination.' > "$HOME/runic-sandbox-inputs/save-target.txt"
cat > "$app/files/bin/runic-usability" <<'EOF'
#!/bin/sh
export GDK_BACKEND=wayland
export RUNIC_TEST_DENIED_FILE="$HOME/runic-sandbox-inputs/private.txt"
exec /app/lib/runic/Runic.Desktop.Gtk4.Smoke --usability
EOF
chmod +x "$app/files/bin/runic-usability"
cat > "$app/metadata" <<'EOF'
[Application]
name=com.runic.tests.Sandbox
runtime=org.gnome.Platform/x86_64/50
sdk=org.gnome.Sdk/x86_64/50
command=runic-usability
EOF
cat > "$app/files/share/applications/com.runic.tests.Sandbox.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Runic Sandbox Test
Exec=runic-usability
Categories=Development;
EOF
flatpak build-finish --socket=wayland --share=network --device=dri "$app"
flatpak build-export "$root/repo" "$app" test
flatpak install --user --assumeyes --reinstall --no-deps "$root/repo" com.runic.tests.Sandbox
flatpak info --user --show-permissions com.runic.tests.Sandbox
printf 'Installed fixture; start it with: flatpak run --user com.runic.tests.Sandbox\n'
