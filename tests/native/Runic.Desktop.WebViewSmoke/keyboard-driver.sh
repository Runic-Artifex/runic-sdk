#!/usr/bin/env bash
# Drives the --keyboard check of a Linux WebView smoke with real input.
#
#   keyboard-driver.sh OUT_DIR SMOKE [ARG...]
#
# SMOKE runs with --keyboard appended. It prints "KEYBOARD SEND <n> <keys>" and
# "KEYBOARD SHOT <n> <name>"; this script presses the keys in the smoke's window, or
# saves OUT_DIR/<name>.png, and answers <n> on the smoke's standard input. The log
# is OUT_DIR/keyboard.log.
#
# KEYBOARD_INPUT selects the input (default: x11 when DISPLAY is set and
# WAYLAND_DISPLAY is not, otherwise wayland):
#   x11      xdotool through XTEST, for example under xvfb-run with GDK_BACKEND=x11,
#            as CI runs it. Screenshots need ImageMagick's import and are skipped
#            without it.
#   wayland  wtype in a wlroots session. Control and the wheel need a virtual pointer:
#            set KEYBOARD_POINTER to a command that accepts "move X Y" and "scroll N"
#            (nix-manager's hyprland-sandbox provides sandbox-pointer), and
#            KEYBOARD_POINTER_X/Y to a point in the window. Screenshots use grim.
set -euo pipefail

out=${1:?usage: keyboard-driver.sh OUT_DIR SMOKE [ARG...]}
shift
mkdir -p "$out"
if [ -z "${KEYBOARD_INPUT:-}" ]; then
  if [ -n "${DISPLAY:-}" ] && [ -z "${WAYLAND_DISPLAY:-}" ]; then KEYBOARD_INPUT=x11; else KEYBOARD_INPUT=wayland; fi
fi
pointer=${KEYBOARD_POINTER:-}

# Each wtype call uploads a keymap first; give the client time to apply it before
# the key, and to see the release before the keyboard goes away.
wayland_key() { wtype -s 250 "$@" -s 150; }

wayland_press() {
  case "$1" in
    Tab) wayland_key -k Tab ;;
    Shift+Tab) wayland_key -M shift -k Tab -m shift ;;
    Ctrl+plus) wayland_key -M ctrl -k plus -m ctrl ;;
    Ctrl+equal) wayland_key -M ctrl -k equal -m ctrl ;;
    Ctrl+minus) wayland_key -M ctrl -k minus -m ctrl ;;
    Ctrl+0) wayland_key -M ctrl -k 0 -m ctrl ;;
    BackSpace) wayland_key -k BackSpace ;;
    type:*) wayland_key "${1#type:}" ;;
    Ctrl+wheel-up | Ctrl+wheel-down)
      if [ -z "$pointer" ]; then
        echo "KEYBOARD_POINTER is required for $1" >&2
        return 1
      fi
      local steps=-1
      [ "$1" = Ctrl+wheel-down ] && steps=1
      # A move to the current position enters no surface, so move twice.
      local x=${KEYBOARD_POINTER_X:-320} y=${KEYBOARD_POINTER_Y:-240}
      $pointer move "$((x - 1))" "$y"
      $pointer move "$x" "$y"
      # Hold Control while the pointer scrolls one notch. A virtual keyboard sets
      # its modifiers itself; the key press lets the page see Control too.
      wayland_key -M ctrl -P Control_L -s 1200 -p Control_L -m ctrl &
      local holder=$!
      sleep 0.8
      $pointer scroll "$steps"
      wait "$holder"
      ;;
    *)
      echo "Unknown keys: $1" >&2
      return 1
      ;;
  esac
}

window=
x11_window() {
  if [ -z "$window" ]; then
    # Without a window manager the smoke's toplevel is the only visible named window.
    window=$(xdotool search --sync --onlyvisible --name . | tail -n 1)
  fi
  xdotool windowfocus --sync "$window" 2>/dev/null || xdotool windowfocus "$window"
}

x11_press() {
  x11_window
  case "$1" in
    Tab) xdotool key --clearmodifiers Tab ;;
    Shift+Tab) xdotool key --clearmodifiers shift+Tab ;;
    Ctrl+plus) xdotool key --clearmodifiers ctrl+plus ;;
    Ctrl+equal) xdotool key --clearmodifiers ctrl+equal ;;
    Ctrl+minus) xdotool key --clearmodifiers ctrl+minus ;;
    Ctrl+0) xdotool key --clearmodifiers ctrl+0 ;;
    BackSpace) xdotool key --clearmodifiers BackSpace ;;
    type:*) xdotool type --delay 80 "${1#type:}" ;;
    Ctrl+wheel-up | Ctrl+wheel-down)
      local button=4
      [ "$1" = Ctrl+wheel-down ] && button=5
      xdotool mousemove --window "$window" 200 200
      sleep 0.2
      xdotool keydown ctrl
      sleep 0.3
      xdotool click "$button"
      sleep 0.3
      xdotool keyup ctrl
      ;;
    *)
      echo "Unknown keys: $1" >&2
      return 1
      ;;
  esac
}

shot() {
  if [ "$KEYBOARD_INPUT" = x11 ]; then
    if command -v import >/dev/null; then import -window root "$1"; fi
  else
    "${KEYBOARD_SCREENSHOT:-grim}" "$1"
  fi
}

coproc smoke { "$@" --keyboard 2>&1; }
pid=$smoke_PID
status=0
while IFS= read -r line; do
  printf '%s\n' "$line" | tee -a "$out/keyboard.log"
  case "$line" in
    "KEYBOARD SEND "*)
      read -r _ _ request keys <<<"$line"
      sleep 0.3
      "${KEYBOARD_INPUT}_press" "$keys" || status=1
      echo "$request" >&"${smoke[1]}"
      ;;
    "KEYBOARD SHOT "*)
      read -r _ _ request name <<<"$line"
      sleep 0.8
      shot "$out/$name.png" || status=1
      echo "$request" >&"${smoke[1]}"
      ;;
  esac
done <&"${smoke[0]}"
wait "$pid" || status=$?
grep -q '^KEYBOARD DONE' "$out/keyboard.log" || status=1
exit "$status"
