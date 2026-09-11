#!/usr/bin/env python3
"""Read back native session services inside the disposable desktop container."""
import json
import os
from pathlib import Path
import subprocess

from gi.repository import Gio, GLib

if Path("/etc/hostname").read_text().strip() not in {"runic-headless-gnome", "runic-headless-kde"}:
    raise SystemExit("Run inside a disposable Runic desktop container")
if os.environ.get("XDG_SESSION_TYPE") == "x11":
    if not Path("/tmp/.X11-unix/X0").is_socket():
        raise SystemExit("Independent Xorg socket is missing")
    subprocess.run(["pgrep", "-x", "Xorg"], check=True, stdout=subprocess.DEVNULL)
else:
    if not (Path("/run/user/1000") / os.environ["WAYLAND_DISPLAY"]).is_socket():
        raise SystemExit("Independent Wayland socket is missing")

bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)


def call(destination, path, interface, method, parameters=None):
    return bus.call_sync(destination, path, interface, method, parameters,
                         None, Gio.DBusCallFlags.NONE, 10000, None).unpack()


desktop = os.environ.get("XDG_CURRENT_DESKTOP", "")
if desktop == "GNOME":
    running = call("org.gnome.SessionManager", "/org/gnome/SessionManager",
                   "org.gnome.SessionManager", "IsSessionRunning")[0]
    if not running:
        raise SystemExit("GNOME session is not running")
    display = call("org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig",
                   "org.gnome.Mutter.DisplayConfig", "GetCurrentState")
    monitors = display[1]
    if not any(monitor[0][2] == "MetaVirtualMonitor" for monitor in monitors):
        raise SystemExit("Mutter did not report a virtual monitor")
elif desktop == "KDE":
    running = call("org.freedesktop.DBus", "/org/freedesktop/DBus",
                   "org.freedesktop.DBus", "NameHasOwner",
                   GLib.Variant("(s)", ("org.kde.plasmashell",)))[0]
    if not running:
        raise SystemExit("Plasma shell is not running")
    monitors = json.loads(subprocess.check_output(["kscreen-doctor", "-j"], text=True, timeout=10))["outputs"]
    if not any(monitor.get("enabled") and monitor.get("connected") for monitor in monitors):
        raise SystemExit("KWin did not report an enabled output")
else:
    raise SystemExit("Expected GNOME or KDE desktop")
setting = call("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
               "org.freedesktop.portal.Settings", "Read",
               GLib.Variant("(ss)", ("org.freedesktop.appearance", "color-scheme")))[0]
subprocess.run(["systemctl", "--user", "is-active", "pipewire.service"],
               check=True, stdout=subprocess.DEVNULL)
bus_id = call("org.freedesktop.DBus", "/org/freedesktop/DBus",
              "org.freedesktop.DBus", "GetId")[0]
print(json.dumps({"desktop": desktop, "session_type": os.environ.get("XDG_SESSION_TYPE"), "session_running": running, "session_bus_id": bus_id,
                  "monitors": monitors, "color_scheme": setting,
                  "pipewire_active": True}, indent=2))
