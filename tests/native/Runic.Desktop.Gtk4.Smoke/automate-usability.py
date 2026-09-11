#!/usr/bin/env python3
"""Exercise the real GTK4 fixture through AT-SPI in a disposable test desktop."""
import argparse
import json
import os
import re
from pathlib import Path
import signal
import subprocess
import time
import traceback

import pyatspi
from speech_audio import inspect_audio
from gi.repository import GLib, Gio


from native_accessibility import tree, applications, unique, action_names, invoke


class Suite:
    def __init__(self, args):
        self.args = args
        self.output = args.output
        self.output.mkdir(parents=True, exist_ok=False)
        self.log = self.output / "fixture.log"
        self.process = None
        self.passed = []
        self.orca = None
        self.speech = []
        self.gnome_input = None
        self.x11_input = None
        self.accessibility_enabled = None

    def wait(self, check, seconds=20):
        deadline = time.monotonic() + seconds
        error = None
        while time.monotonic() < deadline:
            try:
                while GLib.MainContext.default().pending():
                    GLib.MainContext.default().iteration(False)
                result = check()
                if result:
                    return result
            except Exception as caught:
                error = caught
            if self.process and self.process.poll() is not None:
                raise RuntimeError(f"Fixture exited early: {self.process.returncode}")
            time.sleep(0.2)
        raise TimeoutError(f"Condition did not complete in {seconds}s; last error: {error}")

    def fixture(self):
        return unique(applications(), lambda n: "runic" in n.name.lower())

    def button(self, name):
        return unique(tree(self.fixture()), lambda n: n.getRoleName() == "button" and n.name == name)

    def click(self, name):
        invoke(self.wait(lambda: self.button(name)), "press")

    def expect(self, text, offset):
        self.wait(lambda: text in self.log.read_text()[offset:])

    def step(self, button, result):
        offset = len(self.log.read_text())
        self.click(button)
        self.expect(result, offset)

    def passed_check(self, name):
        self.passed.append(name)
        print("PASS " + name, flush=True)

    def set_accessibility(self, enabled=None):
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        if enabled is None:
            self.accessibility_enabled = bus.call_sync("org.a11y.Bus", "/org/a11y/bus",
                "org.freedesktop.DBus.Properties", "Get",
                GLib.Variant("(ss)", ("org.a11y.Status", "IsEnabled")), None, 0, 5000, None).unpack()[0]
            enabled = True
        bus.call_sync("org.a11y.Bus", "/org/a11y/bus", "org.freedesktop.DBus.Properties", "Set",
                      GLib.Variant("(ssv)", ("org.a11y.Status", "IsEnabled", GLib.Variant("b", enabled))),
                      None, 0, 5000, None)

    def picker(self, title):
        if self.args.kde_pickers:
            app = unique(applications(), lambda n: n.name == "xdg-desktop-portal-kde")
            return unique(tree(app), lambda n: n.getRoleName() == "dialog" and n.name == title)
        app = unique(applications(), lambda n: n.name == "org.gnome.Nautilus")
        return unique(tree(app), lambda n: n.getRoleName() == "frame" and n.name == title)

    def choose(self, title, path):
        if self.args.kde_pickers:
            frame = self.wait(lambda: self.picker(title))
            def labelled(n):
                return n.getRoleName() == "combo box" and any(
                    relation.getRelationType() == pyatspi.RELATION_LABELLED_BY and any(
                        relation.getTarget(i).name == "Name:" for i in range(relation.getNTargets()))
                    for relation in n.getRelationSet())
            combo = unique(tree(frame), labelled)
            entry = unique(tree(combo), lambda n: n.getRoleName() == "text")
            if not entry.queryEditableText().setTextContents(str(path)):
                raise RuntimeError("KDE's native filename field rejected the path")
            self.wait(lambda: entry.queryText().getText(0, -1) == str(path))
            button = self.wait(lambda: unique(tree(frame), lambda n: n.getRoleName() == "button"
                and n.name == ("Open" if title == "Open file" else "Save") and "Press" in action_names(n)))
            invoke(button, "Press")
            return
        frame = self.wait(lambda: self.picker(title))
        self.wait(lambda: frame.getState().contains(pyatspi.STATE_ACTIVE))
        toolbar = self.wait(lambda: unique(tree(self.picker(title)), lambda n: n.getRoleName() == "tool bar" and "toolbar.edit-location" in action_names(n)))
        invoke(toolbar, "toolbar.edit-location")
        entry = self.wait(lambda: unique(tree(self.picker(title)), lambda n:
            n.getRoleName() == "text" and "activate" in action_names(n)
            and n.getState().contains(pyatspi.STATE_FOCUSED)))
        if not entry.queryEditableText().setTextContents(str(path.parent) + "/"):
            raise RuntimeError("Native location entry rejected the path")
        self.wait(lambda: entry.queryText().getText(0, -1) == str(path.parent) + "/")
        if self.gnome_input is None:
            raise RuntimeError("GNOME chooser navigation requires compositor input")
        bus, interface, session = self.gnome_input
        for pressed in (True, False):
            bus.call_sync(interface, session, interface + ".Session", "NotifyKeyboardKeycode",
                          GLib.Variant("(ub)", (28, pressed)), None, 0, 5000, None)
            time.sleep(0.05)
        cell = self.wait(lambda: unique(tree(self.picker(title)), lambda n:
            n.getRoleName() == "table cell" and n.name == path.name + ". File"))
        if not cell.parent.querySelection().selectChild(cell.getIndexInParent()):
            raise RuntimeError("Native chooser rejected file selection")
        accept = self.wait(lambda: unique(tree(self.picker(title)), lambda n:
            n.getRoleName() == "button" and n.name in ({"Select"} if title == "Open file" else {"Save", "Replace"})
            and n.getState().contains(pyatspi.STATE_SENSITIVE)))
        invoke(accept, "click")

    def snapshot(self):
        nodes = []
        for app in applications():
            if "runic" in app.name.lower() or app.name in {"org.gnome.Nautilus", "xdg-desktop-portal-kde"}:
                for node in tree(app):
                    nodes.append({"application": app.name, "role": node.getRoleName(),
                                  "name": node.name, "actions": action_names(node)})
                    if len(nodes) >= 2000:
                        break
        (self.output / "accessibility.json").write_text(json.dumps(nodes, indent=2))

    def establish_gnome_input(self):
        # Keep a compositor keyboard alive for the whole headless test: removing
        # its last input device drops the seat focus used by keyboard and Orca.
        # A new headless session starts in the overview. Accessible widget focus
        # alone does not give its window keyboard focus or activate Orca's script.
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        interface = "org.gnome.Mutter.RemoteDesktop"
        session = bus.call_sync(interface, "/org/gnome/Mutter/RemoteDesktop", interface,
                                "CreateSession", None, None, 0, 5000, None).unpack()[0]
        def call(method, parameters=None):
            return bus.call_sync(interface, session, interface + ".Session", method,
                                 parameters, None, 0, 5000, None)
        try:
            call("Start")
            self.gnome_input = (bus, interface, session)
            call("NotifyKeyboardKeycode", GLib.Variant("(ub)", (1, True)))
            time.sleep(0.05)
            call("NotifyKeyboardKeycode", GLib.Variant("(ub)", (1, False)))
            time.sleep(0.3)
            frame = unique(tree(self.fixture()), lambda n: n.getRoleName() == "frame")
            if not frame.getState().contains(pyatspi.STATE_ACTIVE):
                call("NotifyKeyboardKeycode", GLib.Variant("(ub)", (56, True)))
                try:
                    call("NotifyKeyboardKeycode", GLib.Variant("(ub)", (15, True)))
                    time.sleep(0.05)
                    call("NotifyKeyboardKeycode", GLib.Variant("(ub)", (15, False)))
                finally:
                    call("NotifyKeyboardKeycode", GLib.Variant("(ub)", (56, False)))
                self.wait(lambda: frame.getState().contains(pyatspi.STATE_ACTIVE))
        except Exception:
            self.stop_gnome_input()
            raise

    def stop_gnome_input(self):
        if self.gnome_input is not None:
            bus, interface, session = self.gnome_input
            self.gnome_input = None
            bus.call_sync(interface, session, interface + ".Session", "Stop",
                          None, None, 0, 5000, None)

    def kde_input(self):
        if self.args.xorg:
            from xorg_input import XorgInput
            return XorgInput()
        from kde_input import KdeInput
        return KdeInput()

    def check_keyboard(self):
        kde = self.args.kde_keyboard
        query = ["fcitx5-remote", "-n"] if kde else ["ibus", "engine"]
        def engine():
            return subprocess.check_output(query, text=True, timeout=5).strip()
        previous_engine = engine() or ("keyboard-us" if kde else "xkb:us::eng")
        keyboard = None
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        destination = "org.gnome.Mutter.RemoteDesktop"
        session = None
        if kde:
            keyboard = self.kde_input()
        else:
            session = bus.call_sync(destination, "/org/gnome/Mutter/RemoteDesktop", destination,
                                    "CreateSession", None, None, 0, 5000, None).unpack()[0]
        def call(method, signature=None, values=()):
            return bus.call_sync(destination, session, destination + ".Session", method,
                                 GLib.Variant(signature, values) if signature else None,
                                 None, 0, 5000, None)
        def key(code, pressed):
            if kde:
                keyboard.key(code, pressed)
            else:
                call("NotifyKeyboardKeycode", "(ub)", (code, pressed))
        def tap(code):
            key(code, True)
            try:
                time.sleep(0.04)
            finally:
                key(code, False)
            time.sleep(0.04)
        def field(label):
            return unique(tree(self.fixture()), lambda n: n.name == label and n.getRoleName() == "entry")
        def switch_engine(target):
            for _ in range(8):
                if engine() == target:
                    return
                modifier = 29 if kde else 125  # Fcitx Ctrl+Space / GNOME Super+Space.
                key(modifier, True)
                try:
                    tap(57)
                finally:
                    key(modifier, False)
                time.sleep(0.3)
            raise RuntimeError("Could not select the configured input source: " + target)
        native_events = []
        def on_event(event):
            if event.source.getApplication() == self.fixture():
                native_events.append({"type": event.type, "name": event.source.name,
                                      "detail1": event.detail1, "detail2": event.detail2})
        event_types = ("object:state-changed:focused", "object:text-changed", "object:text-caret-moved")
        pyatspi.Registry.registerEventListener(on_event, *event_types)
        def observed(kind, label, offset, detail=None):
            return any(e["type"].startswith(kind) and e["name"] == label
                       and (detail is None or e["detail1"] == detail) for e in native_events[offset:])
        try:
            if not kde:
                call("Start")
            name = field("Your name")
            if not name.queryComponent().grabFocus():
                raise RuntimeError("Could not establish the initial keyboard focus")
            self.wait(lambda: name.getState().contains(pyatspi.STATE_FOCUSED))
            previous_engine = self.wait(engine)
            switch_engine("keyboard-us" if kde else "xkb:us::eng")
            text_offset = len(native_events)
            for code in (19, 22, 49, 23, 46):  # evdev: r u n i c
                tap(code)
            self.wait(lambda: name.queryText().getText(0, -1) == "runic")
            self.wait(lambda: observed("object:text-changed:insert", "Your name", text_offset))
            self.wait(lambda: observed("object:text-caret-moved", "Your name", text_offset, 5))
            # Walk every form control in both directions through the compositor.
            # AT-SPI focus state and the independently delivered event must agree.
            order = [name, field("Composition text")] + [self.button(label) for label in (
                "Target hits: 0", "Record snapshot", "Open file", "Choose save destination",
                "Hold inhibition", "Release inhibition", "Close owner during picker", "Finish session")]
            for reverse, targets in ((False, order[1:]), (True, list(reversed(order[:-1])))):
                for target in targets:
                    event_offset = len(native_events)
                    if reverse:
                        key(42, True)
                    try:
                        tap(15)
                    finally:
                        if reverse:
                            key(42, False)
                    self.wait(lambda: target.getState().contains(pyatspi.STATE_FOCUSED))
                    self.wait(lambda: observed("object:state-changed:focused", target.name, event_offset, 1))
            self.passed_check("complete forward/reverse form focus order and native focus events")
            composition = field("Composition text")
            tap(15)  # Tab
            self.wait(lambda: composition.getState().contains(pyatspi.STATE_FOCUSED))
            key(42, True)  # Shift+Tab
            try:
                tap(15)
            finally:
                key(42, False)
            self.wait(lambda: name.getState().contains(pyatspi.STATE_FOCUSED))
            tap(15)
            self.wait(lambda: composition.getState().contains(pyatspi.STATE_FOCUSED))
            self.passed_check(("XTEST" if self.args.xorg else "compositor") + " keyboard typing and Tab/Shift+Tab focus navigation")
            target_engine = "pinyin" if kde else "libpinyin"
            switch_engine(target_engine)
            self.wait(lambda: engine() == target_engine)
            for code in (49, 23, 35, 30, 24):  # n i h a o
                tap(code)
            # Let the real input method publish preedit/candidates before commit.
            time.sleep(0.5)
            tap(57)  # Space commits the selected Pinyin candidate.
            self.wait(lambda: composition.queryText().getText(0, -1) == "你好")
            self.wait(lambda: observed("object:text-changed:insert", "Composition text", 0))
            self.wait(lambda: composition.queryText().caretOffset == 2)
            (self.output / "accessibility-events.json").write_text(json.dumps(native_events, indent=2) + "\n")
            self.passed_check("native text values, insertion events and caret positions")
            offset = len(self.log.read_text())
            self.step("Record snapshot", "RESULT Snapshot recorded.")
            states = [json.loads(line[len("USABILITY "):]) for line in self.log.read_text()[offset:].splitlines()
                      if line.startswith("USABILITY {")]
            if len(states) != 1 or states[0]["text"] != "你好":
                raise RuntimeError("The committed IME text did not reach the application bridge")
            events = states[0]["events"]
            if not any(e["type"] == "compositionstart" for e in events) or not any(
                    e["type"] == "compositionend" and e.get("data") == "你好" for e in events):
                raise RuntimeError("Real IME composition events were not observed")
            (self.output / "keyboard-ime.json").write_text(json.dumps(states[0], ensure_ascii=False, indent=2) + "\n")
            self.passed_check(f"real {'Fcitx5' if kde else 'IBus'} Pinyin composition and commit through {'XTEST' if self.args.xorg else 'compositor'} input")
        finally:
            pyatspi.Registry.deregisterEventListener(on_event, *event_types)
            try:
                if kde:
                    field("Composition text").queryComponent().grabFocus()
                    self.wait(lambda: engine())
                switch_engine(previous_engine)
            finally:
                if keyboard:
                    keyboard.close()
                else:
                    call("Stop")

    def check_gnome_scaling(self):
        bus, destination, session = self.gnome_input
        def input_call(method, parameters):
            bus.call_sync(destination, session, destination + ".Session", method, parameters, None, 0, 5000, None)
        def key(code, pressed):
            input_call("NotifyKeyboardKeycode", GLib.Variant("(ub)", (code, pressed)))
            time.sleep(0.05)
        def state():
            return bus.call_sync("org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig",
                "org.gnome.Mutter.DisplayConfig", "GetCurrentState", None, None, 0, 5000, None).unpack()
        current = state()
        if len(current[1]) != 1 or len(current[2]) != 1 or tuple(current[2][0][:2]) != (0, 0):
            raise RuntimeError("Scaling requires one virtual monitor at the origin")
        monitor = current[1][0]
        mode = unique(monitor[1], lambda m: m[6].get("is-current", False))
        previous = current[2][0][2]
        def apply(scale):
            config = [(0, 0, scale, 0, True, [(monitor[0][0], mode[0], {})])]
            bus.call_sync("org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig",
                "org.gnome.Mutter.DisplayConfig", "ApplyMonitorsConfig",
                GLib.Variant("(uua(iiduba(ssa{sv}))a{sv})", (state()[0], 1, config, {})), None, 0, 5000, None)
            self.wait(lambda: state()[2][0][2] == scale)
        records = []
        try:
            # Establish a deterministic window origin through the real desktop
            # maximize shortcut. The shell's top-bar bounds locate its work area.
            key(125, True)
            try:
                key(103, True)
                key(103, False)
            finally:
                key(125, False)
            for scale in (1, 1.5, 2):
                apply(scale)
                time.sleep(0.5)
                target = self.button("Target hits: " + str(1 + len(records)))
                bounds = target.queryComponent().getExtents(pyatspi.WINDOW_COORDS)
                shell = unique(applications(), lambda a: a.name == "gnome-shell")
                activities = unique(tree(shell), lambda n: n.name == "Activities" and n.getRoleName() == "toggle button")
                bar = activities.queryComponent().getExtents(pyatspi.DESKTOP_COORDS)
                document = unique(tree(self.fixture()), lambda n: n.getRoleName() == "document web")
                embedding = document.parent
                while embedding is not None and embedding.getRoleName() != "panel":
                    embedding = embedding.parent
                if embedding is None:
                    raise RuntimeError("Could not locate the WebView's native embedding panel")
                origin = embedding.queryComponent().getExtents(pyatspi.WINDOW_COORDS)
                x = origin.x + bounds.x + bounds.width / 2
                y = bar.y + bar.height + origin.y + bounds.y + bounds.height / 2
                print(f"SCALE {scale} pointer {x},{y}; top bar {bar.height}", flush=True)
                # Home at the right edge to avoid GNOME's top-left hot corner.
                # Clamp each axis separately; a diagonal may stop at one edge.
                input_call("NotifyPointerMotionRelative", GLib.Variant("(dd)", (100000., 0.)))
                time.sleep(0.05)
                input_call("NotifyPointerMotionRelative", GLib.Variant("(dd)", (0., -100000.)))
                time.sleep(0.05)
                input_call("NotifyPointerMotionRelative", GLib.Variant("(dd)", (x - (mode[1] / scale - 1), y)))
                time.sleep(0.1)
                for pressed in (True, False):
                    input_call("NotifyPointerButton", GLib.Variant("(ib)", (272, pressed)))
                    time.sleep(0.05)
                self.wait(lambda: self.button("Target hits: " + str(2 + len(records))))
                offset = len(self.log.read_text())
                self.step("Record snapshot", "RESULT Snapshot recorded.")
                states = [json.loads(line[len("USABILITY "):]) for line in self.log.read_text()[offset:].splitlines()
                          if line.startswith("USABILITY {")]
                if len(states) != 1 or states[0]["hits"] != 2 + len(records):
                    raise RuntimeError("Compositor pointer hit did not reach the application")
                if abs(states[0]["width"] - mode[1] / scale) > 4:
                    raise RuntimeError("Window did not occupy the expected maximized work area")
                records.append({"scale": scale, "display": state(), "bounds": [bounds.x, bounds.y, bounds.width, bounds.height],
                                "pointer": [x, y], "embedding": [origin.x, origin.y, origin.width, origin.height], "top_bar": [bar.x, bar.y, bar.width, bar.height], "page": states[0]})
            (self.output / "scaling.json").write_text(json.dumps(records, indent=2) + "\n")
            self.passed_check("Mutter 100/150/200 percent scales with compositor pointer targeting")
        finally:
            apply(previous)
            # Restore the ordinary window before the portal picker checks.
            key(125, True)
            try:
                key(108, True)
                key(108, False)
            finally:
                key(125, False)

    def check_xorg_scaling(self):
        from kde_input import windows
        # The dummy driver cannot apply RandR transforms. Exercise GTK's real
        # desktop DPI setting instead, retaining the XSETTINGS readback.
        settings = Path.home() / ".config/xsettingsd/xsettingsd.conf"
        previous = settings.read_text()
        daemon = int(subprocess.check_output(["pgrep", "-x", "xsettingsd"], text=True).strip())
        def state():
            return subprocess.check_output(["dump_xsettings"], text=True, timeout=5)
        def apply(scale):
            values = {"Gdk/UnscaledDPI": round(96 * 1024 * scale), "Gdk/WindowScalingFactor": 1}
            text = previous
            for name, value in values.items():
                text = re.sub(r"^" + re.escape(name) + r" .*\n?", "", text, flags=re.M)
                text += f"{name} {value}\n"
            settings.write_text(text)
            os.kill(daemon, signal.SIGHUP)
            self.wait(lambda: all(f"{name} {value}" in state().splitlines() for name, value in values.items()))
            time.sleep(0.5)
        records = []
        keyboard = self.kde_input()
        def window_state():
            return unique(windows(), lambda w: "runic" in w["resourceClass"].lower() and w["caption"] == "Runic Desktop")
        def toggle_maximize():
            frame = window_state()["frame"]
            for _ in range(2):
                keyboard.click(frame["x"] + frame["width"] / 2, frame["y"] + 10)
            time.sleep(0.5)
        try:
            toggle_maximize()
            for scale in (1, 1.5, 2):
                apply(scale)
                actual = state()
                target = self.button("Target hits: " + str(1 + len(records)))
                bounds = target.queryComponent().getExtents(pyatspi.WINDOW_COORDS)
                document = unique(tree(self.fixture()), lambda n: n.getRoleName() == "document web")
                document_bounds = document.queryComponent().getExtents(pyatspi.WINDOW_COORDS)
                window = window_state()
                if min(document_bounds.width, window["client"]["width"], bounds.width, bounds.height) <= 0:
                    raise RuntimeError("Invalid Xorg target/client geometry")
                # Unlike Wayland buffers, Xorg frames include server decorations.
                ratio = document_bounds.width / window["client"]["width"]
                x = window["client"]["x"] + (bounds.x + bounds.width / 2) / ratio
                y = window["client"]["y"] + (bounds.y + bounds.height / 2) / ratio
                print(f"XORG DPI {scale}: target={bounds} document={document_bounds} window={window} pointer={x},{y}; desktop={target.queryComponent().getExtents(pyatspi.DESKTOP_COORDS)}", flush=True)
                keyboard.click(x, y)
                self.wait(lambda: self.button("Target hits: " + str(2 + len(records))))
                offset = len(self.log.read_text())
                self.step("Record snapshot", "RESULT Snapshot recorded.")
                states = [json.loads(line[len("USABILITY "):]) for line in self.log.read_text()[offset:].splitlines()
                          if line.startswith("USABILITY {")]
                if len(states) != 1 or states[0]["hits"] != 2 + len(records):
                    raise RuntimeError("XTEST pointer hit did not reach the application")
                records.append({"desktop_dpi": 96 * scale, "xsettings": actual, "window": window,
                                "pointer": [x, y], "page": states[0]})
            (self.output / "scaling.json").write_text(json.dumps(records, indent=2) + "\n")
            if any(abs(record["page"]["dpr"] - record["desktop_dpi"] / 96) > 0.01 for record in records):
                raise RuntimeError("Desktop DPI changes did not reach the WebView")
            self.passed_check("Xorg 96/144/192 desktop DPI with XTEST pointer targeting")
        finally:
            try:
                settings.write_text(previous)
                os.kill(daemon, signal.SIGHUP)
                toggle_maximize()
            finally:
                keyboard.close()

    def check_kde_scaling(self):
        from kde_input import KdeInput, windows
        def output():
            outputs = json.loads(subprocess.check_output(["kscreen-doctor", "-j"], text=True, timeout=5))["outputs"]
            return unique(outputs, lambda n: n["enabled"] and n["connected"])
        monitor = output()
        previous = monitor["scale"]
        records = []
        try:
            for scale in (1, 1.5, 2):
                subprocess.run(["kscreen-doctor", f"output.{monitor['id']}.scale.{scale}"], check=True, timeout=5,
                               stdout=subprocess.DEVNULL)
                self.wait(lambda: output()["scale"] == scale)
                time.sleep(0.5)
                # Output changes recreate EIS devices/regions; use the new mapping.
                keyboard = KdeInput()
                try:
                    target = self.button("Target hits: " + str(1 + len(records)))
                    bounds = target.queryComponent().getExtents(pyatspi.WINDOW_COORDS)
                    x, y, width, height = bounds.x, bounds.y, bounds.width, bounds.height
                    if min(x, y) < 0 or min(width, height) <= 0:
                        raise RuntimeError(f"Invalid native target bounds: {bounds}")
                    window = unique(windows(), lambda w: "runic" in w["resourceClass"].lower() and w["caption"] == "Runic Desktop")
                    # GTK's Wayland accessibility coordinates are local to its
                    # surface. KWin's buffer origin supplies desktop placement.
                    coordinate_scale = 1
                    if self.args.x11:
                        document = unique(tree(self.fixture()), lambda n: n.getRoleName() == "document web")
                        document_bounds = document.queryComponent().getExtents(pyatspi.WINDOW_COORDS)
                        if document_bounds.width <= 0 or window["buffer"]["width"] <= 0:
                            raise RuntimeError("Invalid X11 document/window geometry")
                        # GTK changes its integer scale at 200%; Xwayland also
                        # has a fractional scale. Derive the mapping from the
                        # full-width WebView and the compositor client buffer.
                        coordinate_scale = document_bounds.width / window["buffer"]["width"]
                        print(f"DOCUMENT {document_bounds}; coordinate scale {coordinate_scale}", flush=True)
                    click_x = window["buffer"]["x"] + (x + width / 2) / coordinate_scale
                    click_y = window["buffer"]["y"] + (y + height / 2) / coordinate_scale
                    print(f"TARGET {bounds}; WINDOW {window}", flush=True)
                    print(f"SCALE {scale} pointer {click_x},{click_y}", flush=True)
                    keyboard.click(click_x, click_y)
                    self.wait(lambda: self.button("Target hits: " + str(2 + len(records))))
                    offset = len(self.log.read_text())
                    self.step("Record snapshot", "RESULT Snapshot recorded.")
                    states = [json.loads(line[len("USABILITY "):]) for line in self.log.read_text()[offset:].splitlines()
                              if line.startswith("USABILITY {")]
                    if len(states) != 1 or states[0]["hits"] != 2 + len(records):
                        raise RuntimeError("Compositor pointer hit did not reach the application")
                    records.append({"scale": scale, "output": output(), "bounds": [x, y, width, height],
                                    "window": window, "coordinate_scale": coordinate_scale, "pointer": [click_x, click_y], "page": states[0]})
                finally:
                    keyboard.close()
            (self.output / "scaling.json").write_text(json.dumps(records, indent=2) + "\n")
            self.passed_check("KWin 100/150/200 percent scales with compositor pointer targeting")
        finally:
            subprocess.run(["kscreen-doctor", f"output.{monitor['id']}.scale.{previous}"], check=True, timeout=5,
                           stdout=subprocess.DEVNULL)
            self.wait(lambda: output()["scale"] == previous)

    def start_orca(self):
        if subprocess.run(["pgrep", "-x", "orca"], stdout=subprocess.DEVNULL).returncode == 0:
            raise RuntimeError("Close the existing Orca instance first; this runner only owns its own reader")
        with (self.output / "orca-process.log").open("w") as log:
            self.orca = subprocess.Popen(["orca", "--debug", "--debug-file", str(self.output / "orca.debug")],
                                         stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        # Orca buffers its debug file; wait on its real service registration.
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        self.wait(lambda: bus.call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus",
                  "org.freedesktop.DBus", "NameHasOwner", GLib.Variant("(s)", ("org.gnome.Orca.Service",)),
                  None, Gio.DBusCallFlags.NONE, 5000, None).unpack()[0], 30)

    def check_orca(self):
        nodes = json.loads(subprocess.check_output(["pw-dump"], text=True, timeout=5))
        sinks = [n["info"]["props"]["node.name"] for n in nodes
                 if n.get("info", {}).get("props", {}).get("media.class") == "Audio/Sink"]
        sink = self.args.audio_sink
        if sink is None:
            if len(sinks) != 1:
                raise RuntimeError("Expected one VM audio sink; choose one explicitly with --audio-sink")
            sink = sinks[0]
        if sink not in sinks:
            raise RuntimeError("Requested audio sink does not exist")
        (self.output / "pipewire-before.json").write_text(json.dumps(nodes, indent=2))
        # Force a real focus transition for the first field too; a fresh GTK
        # window may already focus it before Orca observes the active window.
        initial = self.button("Finish session")
        if not initial.queryComponent().grabFocus():
            raise RuntimeError("Could not establish initial screen-reader focus")
        self.wait(lambda: initial.getState().contains(pyatspi.STATE_FOCUSED))
        time.sleep(0.5)
        if self.args.x11:
            # Orca starts reading the new page on X11 and ignores focus changes
            # during Say All. Interrupt it with the same real Ctrl key a user uses.
            self.x11_input.key(29, True)
            time.sleep(0.05)
            self.x11_input.key(29, False)
            time.sleep(0.3)
        for index, (label, role) in enumerate([("Your name", "entry"), ("Composition text", "entry"), ("Open file", "button")]):
            wav = self.output / f"speech-{index}.wav"
            capture_name = f"runic-orca-capture-{os.getpid()}-{index}"
            props = json.dumps({"stream.capture.sink": True, "node.name": capture_name})
            with (self.output / f"capture-{index}.log").open("w") as log:
                capture = subprocess.Popen(["pw-record", "--target", sink, "-P", props,
                                            "--rate", "16000", "--channels", "1", "--format", "s16",
                                            "--sample-count", "128000", str(wav)],
                                           stdout=log, stderr=subprocess.STDOUT)
            try:
                def linked():
                    if capture.poll() is not None:
                        raise RuntimeError("PipeWire recorder exited before focus")
                    graph = json.loads(subprocess.check_output(["pw-dump"], text=True, timeout=5))
                    return any(n.get("info", {}).get("props", {}).get("node.name") == capture_name
                               and n["info"].get("state") == "running" for n in graph)
                self.wait(linked, 10)
                control = unique(tree(self.fixture()), lambda n: n.name == label and n.getRoleName() == role)
                if not control.queryComponent().grabFocus():
                    raise RuntimeError("Native focus was rejected: " + label)
                self.wait(lambda: control.getState().contains(pyatspi.STATE_FOCUSED))
                capture_exit = capture.wait(timeout=12)
                # pw-cat 1.6.8 exits 1 at its sample limit: only playback drain
                # sets EXIT_SUCCESS upstream. Require a complete PCM recording
                # and reject diagnostics rather than treating every exit 1 as OK.
                diagnostics = (self.output / f"capture-{index}.log").read_text().strip()
                if capture_exit not in (0, 1) or diagnostics != str(wav):
                    raise RuntimeError("PipeWire recording failed: " + diagnostics)
            finally:
                if capture.poll() is None:
                    capture.send_signal(signal.SIGINT)
                    try:
                        capture.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        capture.kill()
                        capture.wait(timeout=3)
            metrics = inspect_audio(wav)
            if metrics["duration_seconds"] != 8:
                raise RuntimeError("PipeWire did not capture the complete sample count")
            self.speech.append({"label": label, "role": role, "audio": wav.name, "sink": sink,
                                "recorder_exit_code": capture_exit, "metrics": metrics})
        # Stopping our reader flushes its own debug file. These are actual Orca
        # speech requests; DOM text or a Whisper transcript cannot substitute.
        self.stop_orca()
        debug = (self.output / "orca.debug").read_text()
        spoken = [line.split("SPEECH OUTPUT: ", 1)[1] for line in debug.splitlines() if "SPEECH OUTPUT: " in line]
        for item in self.speech:
            for expected in (item["label"], item["role"]):
                if not any(line.startswith((repr(expected) + " ", repr(expected + ".") + " ")) for line in spoken):
                    raise RuntimeError("Orca did not request the expected speech: " + expected)
        (self.output / "speech.json").write_text(json.dumps({"captures": self.speech, "orca_output": spoken,
            "orca_version": subprocess.check_output(["orca", "--version"], text=True, timeout=10).strip(),
            "pipewire_version": subprocess.check_output(["pw-record", "--version"], text=True, timeout=5).strip()}, indent=2) + "\n")
        self.passed_check("native focus and Orca label/role speech with recorded PipeWire audio")

    def stop_orca(self):
        if self.orca and self.orca.poll() is None:
            os.killpg(self.orca.pid, signal.SIGTERM)
            try:
                self.orca.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(self.orca.pid, signal.SIGKILL)
                self.orca.wait(timeout=5)

    def gnome_inhibitors(self):
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        value = bus.call_sync("org.gnome.SessionManager", "/org/gnome/SessionManager",
                              "org.gnome.SessionManager", "GetInhibitors", None, None,
                              Gio.DBusCallFlags.NONE, 5000, None)
        return set(value.unpack()[0])

    def kde_inhibitors(self):
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        value = bus.call_sync("org.kde.Solid.PowerManagement", "/org/kde/Solid/PowerManagement/PolicyAgent",
            "org.freedesktop.DBus.Properties", "Get",
            GLib.Variant("(ss)", ("org.kde.Solid.PowerManagement.PolicyAgent", "ActiveInhibitions")),
            None, Gio.DBusCallFlags.NONE, 5000, None)
        return {tuple(inhibition) for inhibition in value.unpack()[0]}

    def run(self):
        failure = None
        try:
            self.set_accessibility()
            if any("runic" in a.name.lower() for a in applications()):
                raise RuntimeError("Close other Runic fixtures before running this suite")
            if self.args.orca:
                self.start_orca()
            with self.log.open("w") as log:
                self.process = subprocess.Popen(self.args.command, stdout=log, stderr=subprocess.STDOUT,
                                                start_new_session=True)
            self.wait(lambda: "USABILITY READY" in self.log.read_text(), self.args.startup_timeout)
            required = {"Your name", "Composition text", "Open file", "Finish session"}
            self.wait(lambda: required <= {n.name for n in tree(self.fixture())})
            if "GNOME" in os.environ.get("XDG_CURRENT_DESKTOP", "").upper():
                self.establish_gnome_input()
            if self.args.x11:
                # Xwayland requires a live seat for application activation and
                # Orca's initial focus. Keep it alive until the fixture exits.
                self.x11_input = self.kde_input()
                frame = unique(tree(self.fixture()), lambda n: n.getRoleName() == "frame")
                self.wait(lambda: frame.getState().contains(pyatspi.STATE_ACTIVE))
            self.snapshot()
            if self.args.x11:
                clients = subprocess.check_output(["xprop", "-root", "_NET_CLIENT_LIST"], text=True, timeout=5)
                matches = []
                for identifier in re.findall(r"0x[0-9a-fA-F]+", clients):
                    properties = subprocess.check_output(["xprop", "-id", identifier,
                        "WM_CLASS", "_NET_WM_NAME", "_NET_WM_PID"], text=True, timeout=5)
                    if '"Runic Desktop"' in properties and "runic" in properties.lower():
                        matches.append({"id": identifier, "properties": properties})
                if len(matches) != 1:
                    raise RuntimeError(f"Expected one Runic X11 window, found {matches}")
                (self.output / "x11-window.json").write_text(json.dumps(matches[0], indent=2) + "\n")
                self.passed_check("X server confirms the GTK fixture is an X11 client")
            self.passed_check("native accessible control names")
            if self.args.orca:
                self.check_orca()
            if self.args.gnome_keyboard or self.args.kde_keyboard:
                self.check_keyboard()
            self.click("Target hits: 0")
            self.wait(lambda: self.button("Target hits: 1"))
            offset = len(self.log.read_text())
            self.step("Record snapshot", "RESULT Snapshot recorded.")
            states = [json.loads(line[len("USABILITY "):]) for line in self.log.read_text()[offset:].splitlines()
                      if line.startswith("USABILITY {")]
            if len(states) != 1 or states[0]["hits"] != 1:
                raise RuntimeError("Accessible target action did not reach the page")
            self.passed_check("native action reaches WebView and snapshot")
            if self.args.kde_scaling:
                if self.args.xorg:
                    self.check_xorg_scaling()
                else:
                    self.check_kde_scaling()
            if self.args.gnome_scaling:
                self.check_gnome_scaling()
            is_gnome = "GNOME" in os.environ.get("XDG_CURRENT_DESKTOP", "").upper()
            inhibitors = self.gnome_inhibitors if is_gnome else self.kde_inhibitors
            desktop = "GNOME" if is_gnome else "PowerDevil"
            before = inhibitors()
            self.step("Hold inhibition", "RESULT Inhibition request held.")
            held = self.wait(lambda: inhibitors() - before)
            if len(held) != 1:
                raise RuntimeError(f"Expected one additional {desktop} inhibitor")
            self.step("Release inhibition", "RESULT Inhibition released.")
            self.wait(lambda: not (held & inhibitors()))
            self.passed_check(f"{desktop} registers and removes the native inhibition request")
            self.passed_check("portal inhibition acquire and release")
            if self.args.gnome_pickers or self.args.kde_pickers:
                inputs = Path.home() / "runic-sandbox-inputs"
                target = inputs / "save-target.txt"
                original = target.read_bytes()
                siblings = sorted(p.name for p in inputs.iterdir())
                if "SANDBOX PASS direct access" not in self.log.read_text():
                    raise RuntimeError("Sandbox denial was not exercised; use the Flatpak fixture")
                offset = len(self.log.read_text())
                self.click("Open file")
                self.choose("Open file", inputs / "granted.txt")
                self.expect("RESULT Opened granted.txt: Runic granted sandbox document.", offset)
                self.expect("SANDBOX PASS direct access", offset)
                self.passed_check("portal grant reads document while sibling remains denied")
                offset = len(self.log.read_text())
                self.click("Open file")
                frame = self.wait(lambda: self.picker("Open file"))
                if self.args.kde_pickers:
                    invoke(unique(tree(frame), lambda n: n.getRoleName() == "button" and n.name == "Cancel"), "Press")
                else:
                    invoke(frame, "window.close")
                self.expect("RESULT Dismissed", offset)
                self.passed_check("native chooser cancellation")
                offset = len(self.log.read_text())
                self.click("Choose save destination")
                self.choose("Save file", target)
                # Selecting an existing destination may require a native replace confirmation.
                def save_result():
                    if "AtomicReplaceUnavailable" in self.log.read_text()[offset:]:
                        return True
                    if self.args.kde_pickers:
                        app = unique(applications(), lambda n: n.name == "xdg-desktop-portal-kde")
                        alerts = [n for n in tree(app) if n.getRoleName() == "dialog" and n.name == "Overwrite File?"]
                        buttons = [n for alert in alerts for n in tree(alert) if n.getRoleName() == "button" and n.name == "Overwrite"]
                        if len(buttons) == 1:
                            invoke(buttons[0], "Press")
                        return False
                    app = unique(applications(), lambda n: n.name == "org.gnome.Nautilus")
                    alerts = [n for n in tree(app) if n.getRoleName() == "alert" and n.name == "Replace When Saving?"]
                    buttons = [n for alert in alerts for n in tree(alert) if n.getRoleName() == "button" and n.name == "Replace"]
                    if len(buttons) == 1:
                        invoke(buttons[0], "click")
                    return False
                self.wait(save_result)
                if target.read_bytes() != original or sorted(p.name for p in inputs.iterdir()) != siblings:
                    raise RuntimeError("Atomic-write rejection changed the destination or its siblings")
                self.passed_check("atomic replacement rejected without modifying host files")
            offset = len(self.log.read_text())
            self.click("Close owner during picker")
            self.expect("OWNER CLOSE RESULT Unavailable { Reason = OwnerClosed }", offset)
            if self.process.wait(timeout=20) != 0:
                raise RuntimeError("Fixture failed after closing its owner")
            self.passed_check("pending picker invalidated when owner closes")
        except Exception as error:
            failure = f"{type(error).__name__}: {error}"
            (self.output / "failure.txt").write_text(traceback.format_exc())
            try:
                self.snapshot()
            except Exception:
                pass
        finally:
            self.stop_orca()
            if self.x11_input is not None:
                try:
                    self.x11_input.close()
                except Exception as error:
                    failure = failure or "X11 input cleanup failed: " + str(error)
            try:
                self.stop_gnome_input()
            except Exception as error:
                failure = failure or "Compositor input cleanup failed: " + str(error)
            if self.process and self.process.poll() is None:
                os.killpg(self.process.pid, signal.SIGTERM)
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(self.process.pid, signal.SIGKILL)
                    self.process.wait(timeout=5)
            if self.accessibility_enabled is not None:
                try:
                    self.set_accessibility(self.accessibility_enabled)
                except Exception as error:
                    failure = failure or "Accessibility cleanup failed: " + str(error)
            report = {"passed": self.passed, "failure": failure,
                      "desktop": os.environ.get("XDG_CURRENT_DESKTOP"),
                      "display_backend": "x11 (Xorg)" if self.args.xorg else "x11 (Xwayland)" if self.args.x11 else "wayland",
                      "manual": ["spoken announcement quality", "visual IME candidate placement",
                                 "physical pointer targeting at desktop scales", "notification focus"]}
            if not (self.args.gnome_keyboard or self.args.kde_keyboard):
                report["manual"].append("real keyboard navigation and IME composition")
            (self.output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
        if failure:
            raise SystemExit("FAIL " + failure)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New directory for logs and results")
    pickers = parser.add_mutually_exclusive_group()
    pickers.add_argument("--kde-pickers", action="store_true", help="Exercise the KDE Flatpak grant/save/cancel dialogs")
    pickers.add_argument("--gnome-pickers", action="store_true", help="Also exercise Nautilus Flatpak grant/save/cancel dialogs")
    keyboards = parser.add_mutually_exclusive_group()
    keyboards.add_argument("--kde-keyboard", action="store_true", help="Exercise KWin EIS keyboard navigation and real Fcitx5 Pinyin")
    keyboards.add_argument("--gnome-keyboard", action="store_true", help="Exercise compositor keyboard navigation and real IBus Pinyin")
    scales = parser.add_mutually_exclusive_group()
    scales.add_argument("--gnome-scaling", action="store_true", help="Check actual Mutter scales and pointer targeting")
    scales.add_argument("--kde-scaling", action="store_true", help="Check actual KWin scales and EIS pointer targeting")
    parser.add_argument("--xorg", action="store_true", help="Use the private standalone Xorg server and XTEST input")
    parser.add_argument("--x11", action="store_true", help="Verify the fixture uses X11 under the guest Xwayland server")
    parser.add_argument("--orca", action="store_true", help="Verify native focus, Orca speech and captured desktop sink audio")
    parser.add_argument("--audio-sink", help="Explicit PipeWire sink name; never captures the microphone")
    parser.add_argument("--startup-timeout", type=int, default=300)
    parser.add_argument("--timeout", type=int, default=600, help="Overall deadline, including startup")
    parser.add_argument("command", nargs=argparse.REMAINDER, help="Fixture command after --")
    args = parser.parse_args()
    if args.xorg:
        args.x11 = True
        if os.environ.get("XDG_SESSION_TYPE") != "x11":
            parser.error("--xorg requires the standalone Xorg session")
    if args.command[:1] == ["--"]:
        args.command.pop(0)
    if not args.command:
        parser.error("A fixture launch command is required")
    if Path("/etc/hostname").read_text().strip() not in {"runic-portal", "runic-headless-gnome", "runic-headless-kde"}:
        parser.error("Run only inside a disposable Runic test VM or container")
    desktop = os.environ.get("XDG_CURRENT_DESKTOP", "").upper()
    if (args.x11 or args.kde_keyboard or args.kde_scaling or args.kde_pickers) and desktop != "KDE":
        parser.error("KDE adapters require the KDE desktop")
    if (args.gnome_keyboard or args.gnome_pickers or args.gnome_scaling) and desktop != "GNOME":
        parser.error("GNOME adapters require the GNOME desktop")
    def expired(signum, frame):
        raise TimeoutError("Suite deadline reached or termination requested")
    signal.signal(signal.SIGALRM, expired)
    signal.signal(signal.SIGTERM, expired)
    signal.alarm(args.timeout)
    Suite(args).run()
    signal.alarm(0)
