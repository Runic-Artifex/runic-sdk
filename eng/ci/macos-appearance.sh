#!/usr/bin/env bash
# W315-011 on the macOS runner: writes appearance preferences, starts the NativeAppearance case in a new process for
# each set (accent colour, Increase contrast and Reduce motion are read at process start), and switches dark mode live
# in the first run. The runner's account may write com.apple.universalaccess. Every preference is restored on exit.
# Usage: macos-appearance.sh <test command>   (for example "dotnet path/to/Runic.Platform.Runtime.Tests.dll")
set -euo pipefail
test "$(uname)" = Darwin
tests=$1
restore() {
  defaults delete -g AppleAccentColor 2>/dev/null || true
  defaults write com.apple.universalaccess increaseContrast -bool false
  defaults write com.apple.universalaccess reduceMotion -bool "$motion_before"
  osascript -e 'tell application "System Events" to tell appearance preferences to set dark mode to false' || true
}
motion_before=$(defaults read com.apple.universalaccess reduceMotion 2>/dev/null || echo 0)
[ "$motion_before" = 1 ] && motion_before=true || motion_before=false
trap restore EXIT
run() { RUNIC_TEST_APPEARANCE=1 $tests --treenode-filter "/*/*/*/*[Category=NativeAppearance]" --output detailed "$@"; }

# The system blue accent, no contrast, no reduced motion; dark mode switched while the process runs.
defaults delete -g AppleAccentColor 2>/dev/null || true
defaults write com.apple.universalaccess increaseContrast -bool false
defaults write com.apple.universalaccess reduceMotion -bool false
run --test-parameter expect-accent=0,0.478,1 --test-parameter expect-contrast=false --test-parameter expect-motion=false --test-parameter live-dark=true
# The orange accent (AppleAccentColor 1) and Reduce motion.
defaults write -g AppleAccentColor -int 1
defaults write com.apple.universalaccess reduceMotion -bool true
run --test-parameter expect-accent=0.969,0.510,0.106 --test-parameter expect-contrast=false --test-parameter expect-motion=true
# Increase contrast.
defaults write com.apple.universalaccess increaseContrast -bool true
run --test-parameter expect-contrast=true --test-parameter expect-motion=true
