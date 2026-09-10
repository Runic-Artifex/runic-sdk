#!/usr/bin/env python3
"""Exercise the real GTK4 fixture through AT-SPI in a disposable test VM."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import time
import traceback

import pyatspi
from speech_audio import inspect_audio
from gi.repository import GLib, Gio


def tree(root, depth=0):
    if depth > 40:
        return
    yield root
    for child in root:
        if child is not None:
            yield from tree(child, depth + 1)


def applications():
    return [a for a in pyatspi.Registry.getDesktop(0) if a is not None]


def unique(nodes, predicate):
    matches = [n for n in nodes if predicate(n)]
    if len(matches) != 1:
        raise RuntimeError(f"Expected one accessible match, found {len(matches)}")
    return matches[0]


def action_names(node):
    try:
        actions = node.queryAction()
        return [actions.getName(i) for i in range(actions.nActions)]
    except (NotImplementedError, GLib.Error):
        return []


def invoke(node, name):
    names = action_names(node)
    if name not in names or not node.queryAction().doAction(names.index(name)):
        raise RuntimeError(f"Native action rejected: {node.name}: {name}")


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

    def wait(self, check, seconds=20):
        deadline = time.monotonic() + seconds
        error = None
        while time.monotonic() < deadline:
            try:
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

    def picker(self, title):
        app = unique(applications(), lambda n: n.name == "org.gnome.Nautilus")
        return unique(tree(app), lambda n: n.getRoleName() == "frame" and n.name == title)

    def choose(self, title, path):
        frame = self.wait(lambda: self.picker(title))
        toolbar = self.wait(lambda: unique(tree(self.picker(title)), lambda n: n.getRoleName() == "tool bar" and "toolbar.edit-location" in action_names(n)))
        invoke(toolbar, "toolbar.edit-location")
        entry = self.wait(lambda: unique(tree(self.picker(title)), lambda n:
            n.getRoleName() == "text" and "activate" in action_names(n)
            and n.getState().contains(pyatspi.STATE_FOCUSED)))
        if not entry.queryEditableText().setTextContents(str(path.parent)):
            raise RuntimeError("Native location entry rejected the path")
        invoke(entry, "activate")
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
            if "runic" in app.name.lower() or app.name == "org.gnome.Nautilus":
                for node in tree(app):
                    nodes.append({"application": app.name, "role": node.getRoleName(),
                                  "name": node.name, "actions": action_names(node)})
                    if len(nodes) >= 2000:
                        break
        (self.output / "accessibility.json").write_text(json.dumps(nodes, indent=2))

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

    def run(self):
        failure = None
        try:
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
            self.snapshot()
            self.passed_check("native accessible control names")
            if self.args.orca:
                self.check_orca()
            self.click("Target hits: 0")
            self.wait(lambda: self.button("Target hits: 1"))
            offset = len(self.log.read_text())
            self.step("Record snapshot", "RESULT Snapshot recorded.")
            states = [json.loads(line[len("USABILITY "):]) for line in self.log.read_text()[offset:].splitlines()
                      if line.startswith("USABILITY {")]
            if len(states) != 1 or states[0]["hits"] != 1:
                raise RuntimeError("Accessible target action did not reach the page")
            self.passed_check("native action reaches WebView and snapshot")
            is_gnome = "GNOME" in os.environ.get("XDG_CURRENT_DESKTOP", "").upper()
            before = self.gnome_inhibitors() if is_gnome else set()
            self.step("Hold inhibition", "RESULT Inhibition request held.")
            if is_gnome:
                held = self.wait(lambda: self.gnome_inhibitors() - before)
                if len(held) != 1:
                    raise RuntimeError("Expected one additional GNOME inhibitor")
            self.step("Release inhibition", "RESULT Inhibition released.")
            if is_gnome:
                self.wait(lambda: not (held & self.gnome_inhibitors()))
                self.passed_check("GNOME registers and removes the native inhibition request")
            self.passed_check("portal inhibition acquire and release")
            if self.args.gnome_pickers:
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
                invoke(self.wait(lambda: self.picker("Open file")), "window.close")
                self.expect("RESULT Dismissed", offset)
                self.passed_check("native chooser cancellation")
                offset = len(self.log.read_text())
                self.click("Choose save destination")
                self.choose("Save file", target)
                # Selecting an existing destination may require a native replace confirmation.
                def save_result():
                    if "AtomicReplaceUnavailable" in self.log.read_text()[offset:]:
                        return True
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
            failure = str(error)
            (self.output / "failure.txt").write_text(traceback.format_exc())
            try:
                self.snapshot()
            except Exception:
                pass
        finally:
            self.stop_orca()
            if self.process and self.process.poll() is None:
                os.killpg(self.process.pid, signal.SIGTERM)
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(self.process.pid, signal.SIGKILL)
                    self.process.wait(timeout=5)
            report = {"passed": self.passed, "failure": failure,
                      "desktop": os.environ.get("XDG_CURRENT_DESKTOP"),
                      "manual": ["spoken announcement quality", "real IME and candidate placement",
                                 "physical pointer targeting at desktop scales", "notification focus"]}
            (self.output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
        if failure:
            raise SystemExit("FAIL " + failure)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New directory for logs and results")
    parser.add_argument("--gnome-pickers", action="store_true", help="Also exercise Nautilus Flatpak grant/save/cancel dialogs")
    parser.add_argument("--orca", action="store_true", help="Verify native focus, Orca speech and captured VM sink audio")
    parser.add_argument("--audio-sink", help="Explicit PipeWire sink name; never captures the microphone")
    parser.add_argument("--startup-timeout", type=int, default=300)
    parser.add_argument("--timeout", type=int, default=600, help="Overall deadline, including startup")
    parser.add_argument("command", nargs=argparse.REMAINDER, help="Fixture command after --")
    args = parser.parse_args()
    if args.command[:1] == ["--"]:
        args.command.pop(0)
    if not args.command:
        parser.error("A fixture launch command is required")
    if Path("/etc/hostname").read_text().strip() != "runic-portal":
        parser.error("Run only inside the disposable runic-portal VM")
    def expired(signum, frame):
        raise TimeoutError("Suite deadline reached or termination requested")
    signal.signal(signal.SIGALRM, expired)
    signal.signal(signal.SIGTERM, expired)
    signal.alarm(args.timeout)
    Suite(args).run()
    signal.alarm(0)
