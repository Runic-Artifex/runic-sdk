"""Patch nixpkgs' xvfb-run for runic-ci's job containers.

1. -nolisten local: network jobs share the host's network namespace, where abstract Unix
   sockets are shared too. Xvfb's abstract socket (@/tmp/.X11-unix/X99) would collide with
   another job's Xvfb of the same number, and clients could reach it. Xvfb listens on the
   container's own /tmp/.X11-unix instead, which the runner creates.
2. Display numbers from 200 up, skipping any whose abstract socket another X server in the
   shared network namespace (an image job's own Xvfb, the host's displays) already has: clients
   try the abstract socket first, so they would reach that server instead.
3. Cleanup tolerates an Xvfb that already exited (`kill` under `set -e`), so the command's own
   exit status is returned.
Usage: xvfb-run.py <xvfb-run-wrapped> <output>
"""
import sys

source, output = sys.argv[1], sys.argv[2]
text = open(source).read()
old = 'LISTENTCP="-nolisten tcp"\n'
assert old in text, "xvfb-run's listen options changed"
text = text.replace(old, 'LISTENTCP="-nolisten tcp -nolisten local"\n')
old = 'SERVERNUM=99\n'
assert old in text, "xvfb-run's default server number changed"
text = text.replace(old, 'SERVERNUM=200\n')
old = "    while [ -f /tmp/.X$i-lock ]; do\n"
assert old in text, "xvfb-run's free display search changed"
text = text.replace(old, "    while [ -f /tmp/.X$i-lock ] || grep -q \"@/tmp/.X11-unix/X$i\\$\" /proc/net/unix; do\n")
assert "\nkill $XVFBPID\n" in text, "xvfb-run's cleanup changed"
text = text.replace("\nkill $XVFBPID\n", "\nkill $XVFBPID 2>/dev/null || true\n")
open(output, "w").write(text)
