#!/usr/bin/env python3
"""Read back native session services inside the disposable GNOME container."""
import json
from pathlib import Path
import subprocess

from gi.repository import Gio, GLib

if Path("/etc/hostname").read_text().strip() != "runic-headless-gnome":
    raise SystemExit("Run inside the disposable runic-headless-gnome container")
if not Path("/run/user/1000/runic-wayland").is_socket():
    raise SystemExit("Independent Wayland socket is missing")

bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)


def call(destination, path, interface, method, parameters=None):
    return bus.call_sync(destination, path, interface, method, parameters,
                         None, Gio.DBusCallFlags.NONE, 10000, None).unpack()


running = call("org.gnome.SessionManager", "/org/gnome/SessionManager",
               "org.gnome.SessionManager", "IsSessionRunning")[0]
if not running:
    raise SystemExit("GNOME session is not running")
display = call("org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig",
               "org.gnome.Mutter.DisplayConfig", "GetCurrentState")
monitors = display[1]
if not any(monitor[0][2] == "MetaVirtualMonitor" for monitor in monitors):
    raise SystemExit("Mutter did not report a virtual monitor")
setting = call("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
               "org.freedesktop.portal.Settings", "Read",
               GLib.Variant("(ss)", ("org.freedesktop.appearance", "color-scheme")))[0]
subprocess.run(["systemctl", "--user", "is-active", "pipewire.service"],
               check=True, stdout=subprocess.DEVNULL)
bus_id = call("org.freedesktop.DBus", "/org/freedesktop/DBus",
              "org.freedesktop.DBus", "GetId")[0]
print(json.dumps({"session_running": running, "session_bus_id": bus_id,
                  "monitors": monitors, "color_scheme": setting,
                  "pipewire_active": True}, indent=2))
