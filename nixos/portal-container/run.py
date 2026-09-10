#!/usr/bin/env python3
"""Boot an isolated managed desktop, run its native suite, collect logs, shut down."""
import argparse
import os
from pathlib import Path
import signal
import subprocess
import tarfile
import time
import uuid

HOST = Path('/run/current-system/sw/bin')
GUEST = '/run/current-system/sw/bin/'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--desktop', choices=['gnome', 'kde'], required=True)
    parser.add_argument('--system', type=Path, required=True, help='Built NixOS system')
    parser.add_argument('--root', type=Path, required=True, help='Prepared foreign-owned disposable root')
    parser.add_argument('--inputs', type=Path, required=True, help='Immutable Nix store directory containing fixtures')
    parser.add_argument('--runtime', type=Path, help='Immutable prepared Flatpak installation')
    parser.add_argument('--output', type=Path, required=True, help='New host results directory')
    parser.add_argument('--flatpak', action='store_true')
    parser.add_argument('--orca', action='store_true')
    parser.add_argument('--notifications', action='store_true', help='Check visible live/cold notification actions')
    parser.add_argument('--keyboard', action='store_true')
    parser.add_argument('--scaling', action='store_true', help='Compositor scale and pointer checks')
    args = parser.parse_args()
    args.root = args.root.resolve(strict=True)
    args.system = args.system.resolve(strict=True)
    args.inputs = args.inputs.resolve(strict=True)
    if args.runtime:
        args.runtime = args.runtime.resolve(strict=True)
    if not (args.system / 'init').is_file():
        parser.error('--system must contain a built NixOS init')
    if args.root == Path('/') or args.root.stat().st_uid != 0x7ffe0000:
        parser.error('--root must be a disposable directory shifted to foreign root ownership')
    for artifact in [args.inputs, args.runtime]:
        if artifact is not None and (not artifact.is_dir() or not artifact.is_relative_to('/nix/store')):
            parser.error('Input/runtime directories must be immutable Nix store paths')
    if args.flatpak and not args.runtime:
        parser.error('--flatpak requires --runtime')
    required = ['install-flatpak.sh', 'Runic.Desktop.Gtk4.Smoke.flatpak'] if args.flatpak else ['Runic.Desktop.Gtk4.Smoke']
    if args.notifications and args.flatpak:
        required.append('Runic.Desktop.Gtk4.Smoke')
    if any(not (args.inputs / name).is_file() for name in required):
        parser.error('Missing fixture inputs: ' + ', '.join(required))
    args.output.mkdir(parents=True, exist_ok=False)
    machine = 'runic-test-' + uuid.uuid4().hex[:12]
    guest_output = '/home/runic/.cache/' + machine
    wayland = 'runic-wayland' if args.desktop == 'gnome' else 'wayland-0'
    process = None
    leader = None

    def command(argv, **kwargs):
        return subprocess.run(argv, check=True, timeout=kwargs.pop('timeout', 30), **kwargs)

    def enter(argv):
        return [str(HOST / 'nsenter'), '-t', str(leader), '-m', '-U', '-p', '-n', '-i', '-u', '--', *argv]

    def guest(argv, timeout=60, **kwargs):
        env = dict(HOME='/home/runic', XDG_RUNTIME_DIR='/run/user/1000',
                   DBUS_SESSION_BUS_ADDRESS='unix:path=/run/user/1000/bus',
                   WAYLAND_DISPLAY=wayland, XDG_SESSION_TYPE='wayland',
                   XDG_CURRENT_DESKTOP='GNOME' if args.desktop == 'gnome' else 'KDE', PATH=GUEST[:-1])
        return command(enter([GUEST + 'systemd-run', '--wait', '--collect', '--pipe', '--uid=runic',
                              '--working-directory=/home/runic',
                              *['--setenv=' + key + '=' + value for key, value in env.items()], *argv]), timeout=timeout, **kwargs)

    def interrupted(signum, frame):
        raise KeyboardInterrupt('Container test interrupted')

    signal.signal(signal.SIGTERM, interrupted)
    try:
        with (args.output / 'boot.log').open('w') as boot:
            launch = [str(HOST / 'systemd-nspawn'), '--user', '--machine=' + machine,
                      '--directory=' + str(args.root), '--private-users=managed',
                      '--private-users-ownership=foreign', '--volatile=yes', '--tmpfs=/usr/bin:mode=0755',
                      '--private-network', '--bind-ro=/nix/store',
                      '--bind-ro=' + str(args.inputs) + ':/run/runic-test-input', '--console=pipe']
            if args.runtime:
                launch.append('--bind-ro=' + str(args.runtime) + ':/var/lib/flatpak')
            launch.append(str(args.system / 'init'))
            print('Starting', machine, 'from', args.system, flush=True)
            process = subprocess.Popen(launch, stdin=subprocess.PIPE, stdout=boot, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError('Container exited during startup; see boot.log')
            result = subprocess.run([str(HOST / 'machinectl'), 'show', machine, '-p', 'Leader', '--value'],
                                    text=True, capture_output=True, timeout=5)
            if result.returncode == 0 and result.stdout.strip().isdigit():
                leader = int(result.stdout.strip())
                probe = subprocess.run(enter([GUEST + 'test', '-S', '/run/user/1000/' + wayland]),
                                       capture_output=True, timeout=5)
                if probe.returncode == 0:
                    break
            time.sleep(1)
        else:
            raise TimeoutError('Container display did not become ready')
        with (args.output / 'session.log').open('w') as log:
            while time.monotonic() < deadline:
                try:
                    guest([GUEST + 'runic-session-probe'], stdout=log, stderr=subprocess.STDOUT, timeout=20)
                    break
                except subprocess.CalledProcessError:
                    time.sleep(2)
            else:
                raise TimeoutError('Desktop/portal session did not become ready')
        with (args.output / 'suite.log').open('w') as log:
            if args.flatpak:
                guest([GUEST + 'bash', '/run/runic-test-input/install-flatpak.sh',
                       '/run/runic-test-input/Runic.Desktop.Gtk4.Smoke.flatpak'], stdout=log, stderr=subprocess.STDOUT)
                fixture = [GUEST + 'flatpak', 'run', '--user', 'com.runic.tests.Sandbox']
            else:
                guest([GUEST + 'cp', '/run/runic-test-input/Runic.Desktop.Gtk4.Smoke', '/home/runic/Runic.Desktop.Gtk4.Smoke'],
                      stdout=log, stderr=subprocess.STDOUT)
                fixture = [GUEST + 'runic-container-fixture', '/home/runic/Runic.Desktop.Gtk4.Smoke', '--usability']
            options = ['--orca'] if args.orca else []
            if args.keyboard:
                options.append('--gnome-keyboard' if args.desktop == 'gnome' else '--kde-keyboard')
            if args.scaling:
                options.append('--kde-scaling' if args.desktop == 'kde' else '--gnome-scaling')
            if args.flatpak:
                options.append('--gnome-pickers' if args.desktop == 'gnome' else '--kde-pickers')
            guest([GUEST + 'runic-portal-automate', '--output', guest_output, '--startup-timeout', '60',
                   *options, '--', *fixture], timeout=660, stdout=log, stderr=subprocess.STDOUT)
            if args.notifications:
                if args.flatpak:
                    guest([GUEST + 'cp', '/run/runic-test-input/Runic.Desktop.Gtk4.Smoke', '/home/runic/Runic.Desktop.Gtk4.Smoke'])
                guest([GUEST + 'runic-notification-automate', '--output', guest_output + '/notifications',
                       '--fixture', '/home/runic/Runic.Desktop.Gtk4.Smoke'], timeout=180, stdout=log, stderr=subprocess.STDOUT)
    finally:
        # Even failed log collection or shutdown must reap our nspawn process.
        try:
            if leader and process and process.poll() is None:
                try:
                    with (args.output / 'journal.log').open('w') as log:
                        command(enter([GUEST + 'journalctl', '-b', '--no-pager', '-n', '500']), stdout=log, stderr=subprocess.STDOUT)
                    archive = args.output / 'guest-results.tar'
                    with archive.open('wb') as stream:
                        result = subprocess.run(enter([GUEST + 'tar', '-C', guest_output, '-cf', '-', '.']),
                                                stdout=stream, stderr=subprocess.DEVNULL, timeout=20)
                    if result.returncode == 0:
                        with tarfile.open(archive) as contents:
                            contents.extractall(args.output / 'results', filter='data')
                        archive.unlink()
                finally:
                    command(enter([GUEST + 'systemctl', 'poweroff']))
        finally:
            if process:
                try:
                    process.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=10)
                finally:
                    if process.stdin:
                        process.stdin.close()

    print('PASS; results:', args.output, flush=True)


if __name__ == '__main__':
    main()
