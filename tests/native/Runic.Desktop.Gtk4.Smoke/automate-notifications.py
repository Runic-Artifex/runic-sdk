#!/usr/bin/env python3
"""Activate visible desktop notification actions and check live/cold GTK focus."""
import argparse
import json
import os
from pathlib import Path
import re
import shlex
import signal
import subprocess
import time
import traceback

import pyatspi
from gi.repository import Gio, GLib
from native_accessibility import applications, tree, unique, action_names, invoke


def wait(check, seconds=30):
    deadline = time.monotonic() + seconds
    error = None
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except Exception as caught:
            error = caught
        time.sleep(0.1)
    raise TimeoutError(f'Notification condition timed out: {error}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, required=True)
    args = parser.parse_args()
    args.fixture = args.fixture.resolve(strict=True)
    if Path('/etc/hostname').read_text().strip() not in {'runic-headless-kde', 'runic-headless-gnome'}:
        parser.error('Run only inside the disposable managed desktop')
    args.output.mkdir(parents=True, exist_ok=False)
    app_id = 'com.runic.tests.Activation'
    receipt = args.output / 'receipt.txt'
    receiver_log = args.output / 'receiver.log'
    launch = ['/run/current-system/sw/bin/runic-container-fixture', str(args.fixture)]
    environment = dict(os.environ, RUNIC_TEST_APP_ID=app_id, RUNIC_TEST_ACTIVATION_RECEIPT=str(receipt))
    service = Path.home() / '.local/share/dbus-1/services' / (app_id + '.service')
    receiver = args.output / 'receiver.sh'
    for path in (service,):
        if path.exists():
            raise RuntimeError('An existing test identity must be cleaned up first: ' + str(path))
        path.parent.mkdir(parents=True, exist_ok=True)
    receiver.write_text('#!/run/current-system/sw/bin/bash\n'
        + 'export RUNIC_TEST_APP_ID=' + shlex.quote(app_id) + '\n'
        + 'export RUNIC_TEST_ACTIVATION_RECEIPT=' + shlex.quote(str(receipt)) + '\n'
        + 'exec ' + shlex.join(launch + ['--notification-receive'])
        + ' > ' + shlex.quote(str(receiver_log)) + ' 2>&1\n')
    receiver.chmod(0o700)
    service.write_text(f'[D-BUS Service]\nName={app_id}\nExec={shlex.quote(str(receiver))}\n')
    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    def accessibility(enabled=None):
        if enabled is None:
            return bus.call_sync('org.a11y.Bus', '/org/a11y/bus', 'org.freedesktop.DBus.Properties',
                'Get', GLib.Variant('(ss)', ('org.a11y.Status', 'IsEnabled')), None, 0, 5000, None).unpack()[0]
        bus.call_sync('org.a11y.Bus', '/org/a11y/bus', 'org.freedesktop.DBus.Properties', 'Set',
            GLib.Variant('(ssv)', ('org.a11y.Status', 'IsEnabled', GLib.Variant('b', enabled))), None, 0, 5000, None)
    if bus.call_sync('org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus',
                     'NameHasOwner', GLib.Variant('(s)', (app_id,)), None, 0, 5000, None).unpack()[0]:
        raise RuntimeError('The notification test identity is already in use')
    previous = accessibility()
    process = None
    gnome_session = None
    pointer_position = None
    results = []
    failure = None
    def gnome_call(method, parameters=None):
        return bus.call_sync('org.gnome.Mutter.RemoteDesktop', gnome_session,
            'org.gnome.Mutter.RemoteDesktop.Session', method, parameters, None, 0, 5000, None)
    def gnome_pointer(target, click=False):
        nonlocal pointer_position
        bounds = target.queryComponent().getExtents(pyatspi.DESKTOP_COORDS)
        if min(bounds.x, bounds.y) < 0 or min(bounds.width, bounds.height) <= 0:
            raise RuntimeError('GNOME notification has no usable stage bounds')
        # Establish the cursor origin once. Moving back out of the banner
        # between hover and click would collapse GNOME's action row.
        if pointer_position is None:
            display = bus.call_sync('org.gnome.Mutter.DisplayConfig', '/org/gnome/Mutter/DisplayConfig',
                'org.gnome.Mutter.DisplayConfig', 'GetCurrentState', None, None, 0, 5000, None).unpack()
            if len(display[1]) != 1 or len(display[2]) != 1 or tuple(display[2][0][:2]) != (0, 0):
                raise RuntimeError('Notification pointer requires one virtual monitor at the origin')
            mode = unique(display[1][0][1], lambda m: m[6].get('is-current', False))
            width = mode[1] / display[2][0][2]
            # Independent axis clamps reach a known corner without entering
            # the overview hot corner or stopping partway along an edge.
            gnome_call('NotifyPointerMotionRelative', GLib.Variant('(dd)', (100000., 0.)))
            time.sleep(0.05)
            gnome_call('NotifyPointerMotionRelative', GLib.Variant('(dd)', (0., -100000.)))
            time.sleep(0.05)
            pointer_position = (width - 1., 0.)
        position = (bounds.x + bounds.width / 2, bounds.y + bounds.height / 2)
        gnome_call('NotifyPointerMotionRelative', GLib.Variant('(dd)',
                   (position[0] - pointer_position[0], position[1] - pointer_position[1])))
        pointer_position = position
        print(f'GNOME pointer {position}; click={click}', flush=True)
        time.sleep(0.15)
        if click:
            for pressed in (True, False):
                gnome_call('NotifyPointerButton', GLib.Variant('(ib)', (272, pressed)))
                time.sleep(0.05)
    try:
        bus.call_sync('org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus',
                      'ReloadConfig', None, None, 0, 5000, None)
        names = bus.call_sync('org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus',
                             'ListActivatableNames', None, None, 0, 5000, None).unpack()[0]
        if app_id not in names:
            raise RuntimeError('The test receiver was not registered for D-Bus activation')
        accessibility(True)
        if os.environ.get('XDG_CURRENT_DESKTOP') == 'GNOME':
            gnome_session = bus.call_sync('org.gnome.Mutter.RemoteDesktop', '/org/gnome/Mutter/RemoteDesktop',
                'org.gnome.Mutter.RemoteDesktop', 'CreateSession', None, None, 0, 5000, None).unpack()[0]
            gnome_call('Start')
            for pressed in (True, False):
                gnome_call('NotifyKeyboardKeycode', GLib.Variant('(ub)', (1, pressed)))
                time.sleep(0.05)
        for cold in (False, True):
            receipt.unlink(missing_ok=True)
            log_path = args.output / ('submit.log' if cold else 'live.log')
            with log_path.open('w') as log:
                process = subprocess.Popen(launch + ['--notification-submit' if cold else '--notification-live'],
                    env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            sender = process.pid
            if cold:
                if process.wait(timeout=20) != 0:
                    raise RuntimeError('Notification sender failed')
            else:
                wait(lambda: 'WAITING pid=' in log_path.read_text())
            def button():
                shell = unique(applications(), lambda a: a.name in {'plasmashell', 'gnome-shell'})
                return unique(tree(shell), lambda n: n.getRoleName() in {'button', 'push button'} and n.name == 'Open result'
                              and n.getState().contains(pyatspi.STATE_SHOWING))
            if gnome_session:
                title = wait(lambda: unique((n for a in applications() if a.name == 'gnome-shell' for n in tree(a)),
                    lambda n: n.name == 'Runic activation test' and n.getState().contains(pyatspi.STATE_SHOWING)))
                gnome_pointer(title)
            target = wait(button)
            names = action_names(target)
            print('Visible notification action:', names, flush=True)
            if gnome_session:
                gnome_pointer(target, click=True)
            else:
                invoke(target, next(name for name in ('Press', 'press', 'click') if name in names))
            value = wait(lambda: receipt.read_text() if receipt.exists() else None)
            match = re.fullmatch(r'pid=(\d+) notification=runic-cold-test action=open token=(True|False) focused=True', value)
            # Xorg Plasma can focus the receiver without an XDG activation token.
            # Keep the token requirement for both native Wayland and Xwayland.
            if not match or (os.environ.get('XDG_SESSION_TYPE') != 'x11' and match[2] != 'True'):
                raise RuntimeError('Receiver did not confirm notification action and native focus: ' + value)
            receiver_pid = int(match[1])
            if (receiver_pid == sender) == cold:
                raise RuntimeError('Receiver process identity does not match live/cold mode')
            if not cold and process.wait(timeout=20) != 0:
                raise RuntimeError('Live notification receiver failed')
            results.append({'cold': cold, 'sender_pid': sender, 'receipt': value,
                            'session_type': os.environ.get('XDG_SESSION_TYPE')})
            print('PASS ' + ('cold' if cold else 'live') + ' notification action and window focus', flush=True)
            wait(lambda: not any(n.name == 'Open result' and n.getState().contains(pyatspi.STATE_SHOWING)
                                  for a in applications() if a.name in {'plasmashell', 'gnome-shell'} for n in tree(a)))
            # Wait for D-Bus ownership to disappear before the next mode.
            wait(lambda: not bus.call_sync('org.freedesktop.DBus', '/org/freedesktop/DBus',
                'org.freedesktop.DBus', 'NameHasOwner', GLib.Variant('(s)', (app_id,)), None, 0, 5000, None).unpack()[0])
    except Exception as error:
        failure = f"{type(error).__name__}: {error}"
        (args.output / 'failure.txt').write_text(traceback.format_exc())
        try:
            nodes = [{'app': a.name, 'role': n.getRoleName(), 'name': n.name, 'actions': action_names(n)}
                     for a in applications() for n in tree(a)]
            (args.output / 'accessibility.json').write_text(json.dumps(nodes, indent=2))
        except GLib.Error as diagnostic_error:
            print('Accessibility diagnostic unavailable:', diagnostic_error, flush=True)
    finally:
        if process and process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=5)
        # A cold receiver belongs to the session bus, not our Popen group.
        # Only terminate it if both the owned bus identity and executable match.
        try:
            pid = bus.call_sync('org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus',
                'GetConnectionUnixProcessID', GLib.Variant('(s)', (app_id,)), None, 0, 5000, None).unpack()[0]
            if Path(f'/proc/{pid}/exe').resolve() == args.fixture.resolve():
                os.kill(pid, signal.SIGTERM)
        except (GLib.Error, ProcessLookupError, FileNotFoundError):
            pass
        if gnome_session:
            gnome_call('Stop')
        accessibility(previous)
        service.unlink()
        (args.output / 'results.json').write_text(json.dumps({'passed': results, 'failure': failure}, indent=2) + '\n')
    if failure:
        raise SystemExit('FAIL ' + failure)


if __name__ == '__main__':
    def expired(signum, frame):
        raise TimeoutError('Notification suite deadline reached or termination requested')
    signal.signal(signal.SIGALRM, expired)
    signal.signal(signal.SIGTERM, expired)
    signal.alarm(120)
    main()
    signal.alarm(0)
