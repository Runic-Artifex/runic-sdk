#!/usr/bin/env bash
# W315-018 on the macOS runner: lets the job control VoiceOver through AppleScript, then runs the given command.
# GitHub's macOS image enables VoiceOver's AppleScript control (SCREnableAppleScript and
# /private/var/db/Accessibility/.VoiceOverAppleScriptEnabled) and grants the runner's agent the Accessibility
# permission, but not the Automation permission to send Apple events to VoiceOver: without it the first osascript call
# waits for a consent prompt that nobody answers. The runner has System Integrity Protection off, so the script grants
# it in the user's TCC database, as @guidepup/setup does, for the processes TCC holds responsible for the job.
# VoiceOver is quit on exit.
# Usage: macos-voiceover.sh <command>...
set -euo pipefail
test "$(uname)" = Darwin
csrutil status | grep -q disabled || { echo "System Integrity Protection is on; the TCC database cannot be written." >&2; exit 1; }
defaults write com.apple.VoiceOver4/default SCREnableAppleScript -bool true
defaults write com.apple.VoiceOverTraining doNotShowSplashScreen -bool true
test -e /private/var/db/Accessibility/.VoiceOverAppleScriptEnabled || sudo sh -c 'printf a > /private/var/db/Accessibility/.VoiceOverAppleScriptEnabled'

now=$(date +%s)
rows=
for client in /opt/hca/hosted-compute-agent /opt/hca/start_hca.sh /usr/local/opt/runner/provisioner/provisioner \
  /usr/local/opt/runner/runprovisioner.sh /bin/bash /bin/zsh /usr/bin/osascript; do
  rows="$rows('kTCCServiceAppleEvents','$client',1,2,3,1,NULL,NULL,0,'com.apple.VoiceOver',NULL,NULL,$now,NULL,NULL,'UNUSED',$now),"
done
columns="service,client,client_type,auth_value,auth_reason,auth_version,csreq,policy_id,indirect_object_identifier_type,indirect_object_identifier,indirect_object_code_identity,flags,last_modified,pid,pid_version,boot_uuid,last_reminded"
sudo sqlite3 -bail "$HOME/Library/Application Support/com.apple.TCC/TCC.db" \
  "BEGIN IMMEDIATE; INSERT INTO access ($columns) VALUES ${rows%,} ON CONFLICT DO UPDATE SET auth_value=excluded.auth_value; COMMIT;"
# tccd reads the database again after a moment.
sleep 1

quit() { osascript -e 'if application "VoiceOver" is running then tell application "VoiceOver" to quit' >/dev/null 2>&1 || true; }
trap quit EXIT
"$@"
