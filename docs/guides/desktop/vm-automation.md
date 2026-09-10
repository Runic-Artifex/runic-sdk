# Desktop VM automation

The first unattended GTK4 runner is implemented in
`tests/native/Runic.Desktop.Gtk4.Smoke/automate-usability.py`. It drives the real
native AT-SPI tree and checks the fixture's observed results. It does not inject
DOM events, mock portal responses, or disable accessibility/WebKit isolation.

## Run in a disposable Linux guest

Build a [portal VM](portal-vm.md) from the current source and run from its graphical
terminal. For serial access, import the desktop environment as described there.
Use a new output directory for each invocation; close other Runic fixtures first.

```sh
runic-portal-automate --output "$HOME/.cache/runic-automation/basic-1" \
  -- runic-portal-test gtk4-usability
```

The runner launches and owns its fixture process, waits for readiness, enforces
an overall ten-minute deadline, and terminates its process group on failure.
`--startup-timeout` (default 300 seconds) accommodates the initial build/restore.
It writes `fixture.log`, `accessibility.json`, and `results.json`; failures also
include a traceback. A failed or timed-out assertion exits nonzero. Outputs are
diagnostic artifacts, not a new release approval requirement.

The common sequence checks accessible control names, a native button action
reaching the WebView, inhibition acquisition/release, and invalidation of a
pending picker when its owner closes. In GNOME it additionally observes the
actual SessionManager inhibitor appearing and disappearing. This verifies a
registered request, not whether the machine physically suspends.

For the standard-runtime Flatpak fixture, follow its
[installation instructions](../../../tests/native/Runic.Desktop.Gtk4.Smoke/flatpak/README.md), then run:

```sh
runic-portal-automate --output "$HOME/.cache/runic-automation/flatpak-1" \
  --gnome-pickers -- flatpak run --user com.runic.tests.Sandbox
```

`--gnome-pickers` currently targets the English Nautilus chooser in the locked
GNOME guest. It navigates through native accessibility interfaces, selects the
fixture file, cancels another request, and accepts the save confirmation. It
checks granted document contents, denied access to the private host sibling,
`AtomicReplaceUnavailable`, unchanged destination bytes, and no extra siblings.
It fails if the sandbox denial check did not run. It only uses the disposable
`~/runic-sandbox-inputs` files created by the installer.

On 2026-09-10 this entire sequence passed in the GNOME Wayland VM using NativeAOT
and `org.gnome.Platform/x86_64/50`, runtime commit
`545da92354a265d2c3572c91c39ac14dd7e74f9d8f9b66744ad50f478d2497c5`.
The first automated run reused the interactive test VM; clean-image repeatability
and the KDE adapter remain follow-ups. Do not infer either from this result.

## Orca and recorded speech

Add `--orca` to the guest command to check native focus and screen-reader audio:

```sh
runic-portal-automate --output "$HOME/.cache/runic-automation/orca-1" \
  --orca --gnome-pickers -- flatpak run --user com.runic.tests.Sandbox
```

This starts an owned Orca instance, focuses **Your name**, **Composition text**
and **Open file** through native AT-SPI, verifies their entry/button roles and
focused state, and captures eight seconds of output for each control. It refuses
to replace an existing Orca instance and stops its own reader/recorder on failure.
The VM's virtual PipeWire sink works without headphones or a physical sound card.
If the VM has multiple sinks, supply `--audio-sink <node.name>` explicitly. The
runner records a sink monitor, never a microphone. See PipeWire's
[pw-record options](https://docs.pipewire.org/page_man_pw-cat_1.html) and
[sink capture property](https://pipewire.pages.freedesktop.org/pipewire/devel/group__pw__keys.html).

The native check requires Orca's actual speech-output records for each label and
role plus complete, sustained, non-silent PCM recordings. A click, silence, or
truncated WAV fails. `speech.json`, `orca.debug`, `pipewire-before.json`, recorder
logs and `speech-*.wav` retain the evidence. The pinned PipeWire 1.6.8 recorder
returns status 1 at its sample limit because its success flag is set on playback
drain; the runner narrowly accepts that case only with the exact sample count
and clean recorder diagnostics. See the
[upstream recorder implementation](https://github.com/PipeWire/pipewire/blob/1.6.8/src/tools/pw-cat.c).

Optionally cross-check those recordings with local CPU Whisper. Build the tool
and checksum-pinned English model on the host, outside the normal SDK shell:

```sh
nix build .#vm-whisper --out-link artifacts/vm-whisper
nix build .#vm-whisper-model --out-link artifacts/vm-whisper-model

# Copy the guest result directory through the VM exchange directory first.
direnv exec . python3 -B tests/native/Runic.Desktop.Gtk4.Smoke/transcribe-speech.py \
  /path/to/copied/orca-1 --model artifacts/vm-whisper-model \
  --whisper artifacts/vm-whisper/bin/whisper-cli \
  --output /path/to/new/transcripts
```

This uses locked whisper.cpp 1.9.2 with base.en, four CPU threads and no cloud
service. The model is an opt-in approximately 148 MB dependency. The script
requires the native checks to have passed, rechecks the WAV, and compares the
recognized label and role. Expected phrases are never passed as recognition
prompts. It retains the transcript and recognizer diagnostics, and returns
nonzero for mismatches; inspect both audio and Orca output before attributing
an ASR mismatch to Runic. Recognition can invent text in non-speech audio, so it
cannot replace the independent native and PCM assertions. See the
[Whisper model card](https://github.com/openai/whisper/blob/main/model-card.md)
and [whisper.cpp](https://github.com/ggml-org/whisper.cpp).

On 2026-09-10, GNOME Wayland with Orca 50.2, speech-dispatcher 0.12.1 and
PipeWire 1.6.8 passed the full Flatpak sequence with the audio extension. Local
Whisper independently recognized all three labels and their entry/button roles.
This verifies focus-triggered speech and acoustic output; it does not test
physical Tab navigation, pronunciation in other languages, or announcement
quality. Those distinctions also apply when no person listens to the recording.

The audio checks have focused negative tests:

```sh
direnv exec . python3 -B -m unittest discover \
  -s tests/native/Runic.Desktop.Gtk4.Smoke -p test_speech_audio.py
```

## Keyboard navigation and real IME

Add `--gnome-keyboard` to exercise GNOME's compositor input path:

```sh
runic-portal-automate --output "$HOME/.cache/runic-automation/keyboard-1" \
  --gnome-keyboard --gnome-pickers -- flatpak run --user com.runic.tests.Sandbox
```

The locked GNOME image supplies US English and Intelligent Pinyin input sources.
The runner owns a Mutter RemoteDesktop session, establishes initial native focus,
types `runic`, checks Tab/Shift+Tab focus movement, switches sources with
Super+Space, types `nihao`, and commits `你好` with Space. It verifies native text,
application bridge output, and real composition start/end events. It restores
the original input source and stops its input session afterward. Run this only
in the disposable test desktop: it generates keyboard input in that session.

This sequence and all existing GNOME Flatpak picker/inhibition assertions passed
on 2026-09-10, including a combined invocation with `--orca` (11 passing checks).
`keyboard-ime.json` retains the observed composition events.
Unlike setting an accessible text value, this exercises the input method. It
uses compositor virtual input, not a physical keyboard or the guest's emulated
USB device. Visual candidate placement and physical device behavior remain
separate checks. The compositor API does not depend on QEMU, making this adapter
usable in a future container desktop too.

## Containers and VMs

The [headless GNOME container experiment](../../../nixos/portal-container/README.md)
has demonstrated a full headless GNOME session, independent Wayland display,
Settings portal and PipeWire in both VM-contained and host-managed nspawn.
The full native usability suite passes in the former, including independently
observed inhibition acquisition/release after installing the fixture desktop ID. On the managed host, WebKit's nested sandbox fails its private proc
mount. Application parity is therefore still unverified.
Keep the VM suite while completing that experiment. A shared-kernel container
is promising for frequent native UI tests; VM coverage remains useful for a
clean boot, graphical login/seat, virtual hardware and kernel-dependent sandbox
behavior. Container results should identify their runtime explicitly.

## Extending the suite

Use the NixOS Python test driver for Linux boot, login, guest commands, QEMU
keyboard/mouse input, screenshots and log collection. The pinned nixpkgs already
contains GNOME and Plasma test examples. Reuse our desktop modules, with separate
KDE/GNOME jobs, rather than automating a maintainer's desktop or installing both
portal stacks in one guest. Prepare the fixture and dependencies before the test
phase; downloads and builds should not consume an interaction timeout. Keep the
standard Flatpak runtime pinned and available to the guest without granting
access to the host Nix store.

| Area | Automation approach and next assertion |
| --- | --- |
| VM lifecycle | Add a NixOS test-driver entry point around the guest runner; boot to Wayland, collect artifacts even on failure, shut down only its own guest. Run one desktop at a time initially. |
| KDE pickers/inhibition | Add an adapter from the actual Qt accessibility tree and observe PowerDevil's native inhibition state. Require the same grant/save/cancel assertions as GNOME. |
| Keyboard and IME | GNOME compositor typing, Tab/Shift+Tab and real IBus Pinyin now pass. Extend to KDE/Fcitx5 and device-level input where needed. Changing an accessible text value does not test an IME. |
| Scaling and targeting | Set actual Mutter/KScreen display scales, read them back, use QEMU pointer input at measured control bounds, and verify hit counts. Retain screenshots for caret/candidate placement. CSS zoom and an AT-SPI button action do not establish physical targeting. |
| Notifications | Activate the visible notification action through the shell UI. Assert the receiver PID and native focused-window result for both live and cold launch. Calling the application's D-Bus callback directly would bypass the activation-token behavior under test. |
| Accessibility | Native roles/focus, Orca speech records, recorded audio and optional local ASR now work in GNOME. Extend to values, physical focus order/events and KDE. Listening remains useful for announcement quality. |
| Windows | Use the existing interactive VM login and Windows UI Automation for WebView2/WinUI dialogs, with the same fixture outcomes and isolated temporary files. Run executable-only NativeAOT publishes and the existing native power-request checks. Session-0 SSH alone cannot cover interactive display behavior. |
| macOS | Add an AXUIElement/Accessibility adapter and native assertions after the real Mac is available. Keep native support explicitly untested until then. |

Start these as focused, opt-in VM jobs. Move stable scenarios into CI after a
fresh-image run establishes their dependencies and failure handling. Preserve
structured results, native logs and failure screenshots; do not add broad soaks,
mandatory manual gates or retry failures until they happen to pass. Visual IME
placement, spoken quality and real power transitions are still outside the
current automated runner's claims.
