#!/usr/bin/env bash
# Drives the --keyboard check of a Linux WebView smoke in a wlroots Wayland session.
#
#   keyboard-wayland.sh OUT_DIR SMOKE [ARG...]
#
# SMOKE runs with --keyboard appended. It prints "KEYBOARD SEND <n> <keys>" and
# "KEYBOARD SHOT <n> <name>"; this script presses the keys in the focused window
# with wtype, or saves OUT_DIR/<name>.png, and answers <n>. Control and the wheel
# need a virtual pointer: set KEYBOARD_POINTER to a command that accepts
# "move X Y" and "scroll N" (nix-manager's hyprland-sandbox provides
# sandbox-pointer). KEYBOARD_SCREENSHOT defaults to grim.
set -euo pipefail

out=${1:?usage: keyboard-wayland.sh OUT_DIR SMOKE [ARG...]}
shift
mkdir -p "$out"
pointer=${KEYBOARD_POINTER:-}
screenshot=${KEYBOARD_SCREENSHOT:-grim}

# Each wtype call uploads a keymap first; give the client time to apply it
# before the key, and to see the release before the keyboard goes away.
key() { wtype -s 250 "$@" -s 150; }

press() {
  case "$1" in
    Tab) key -k Tab ;;
    Shift+Tab) key -M shift -k Tab -m shift ;;
    Ctrl+plus) key -M ctrl -k plus -m ctrl ;;
    Ctrl+equal) key -M ctrl -k equal -m ctrl ;;
    Ctrl+minus) key -M ctrl -k minus -m ctrl ;;
    Ctrl+0) key -M ctrl -k 0 -m ctrl ;;
    BackSpace) key -k BackSpace ;;
    type:*) key "${1#type:}" ;;
    Ctrl+wheel-up | Ctrl+wheel-down)
      if [ -z "$pointer" ]; then
        echo "KEYBOARD_POINTER is required for $1" >&2
        return 1
      fi
      local steps=-1
      [ "$1" = Ctrl+wheel-down ] && steps=1
      # A move to the current position enters no surface, so move twice.
      x=${KEYBOARD_POINTER_X:-320} y=${KEYBOARD_POINTER_Y:-240}
      $pointer move "$((x - 1))" "$y"
      $pointer move "$x" "$y"
      # Hold Control while the pointer scrolls one notch. A virtual keyboard sets
      # its modifiers itself; the key press lets the page see Control too.
      key -M ctrl -P Control_L -s 1200 -p Control_L -m ctrl &
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

coproc smoke { "$@" --keyboard 2>&1; }
pid=$smoke_PID
status=0
while IFS= read -r line; do
  printf '%s\n' "$line" | tee -a "$out/keyboard.log"
  case "$line" in
    "KEYBOARD SEND "*)
      read -r _ _ request keys <<<"$line"
      sleep 0.3
      press "$keys" || status=1
      echo "$request" >&"${smoke[1]}"
      ;;
    "KEYBOARD SHOT "*)
      read -r _ _ request name <<<"$line"
      sleep 0.8
      $screenshot "$out/$name.png" || status=1
      echo "$request" >&"${smoke[1]}"
      ;;
  esac
done <&"${smoke[0]}"
wait "$pid" || status=$?
grep -q '^KEYBOARD DONE' "$out/keyboard.log" || status=1
exit "$status"
