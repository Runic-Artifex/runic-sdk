# Desktop VM automation (deprecated Linux runner)

Use [managed desktop containers](container-automation.md) for new Linux automation.
The commands below remain available for compatibility; the maintained Linux
portal/input/scaling/notification workflow runs in containers.
Windows VM and real macOS testing are unaffected.

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
pending picker when its owner closes. It also observes the
actual GNOME SessionManager or KDE PowerDevil inhibitor appearing and disappearing. This verifies a
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
The first automated run reused the interactive test VM. Fresh-state lifecycle
and the KDE adapter subsequently passed in the managed container runner.

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
used by the managed GNOME container too.

## Containers and VMs

The [managed container runner](container-automation.md) now boots independent
GNOME/Plasma sessions with fresh state, runs the shared suite, collects logs and
shuts down on success or failure. Both desktops support Flatpak chooser/sandbox,
native inhibition, Orca/audio, compositor input/Pinyin, actual scale/pointer
checks and live/cold notification focus. Linux VM helpers are deprecated. No host desktop sockets or physical devices are shared.

## Extending the suite

Extend managed containers for Linux. Prepare immutable fixtures and pinned
runtime dependencies before the interaction phase, and keep GNOME/Plasma jobs
separate. Windows VM and real macOS adapters remain independent workstreams.

| Area                  | Automation approach and next assertion                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| --------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Keyboard and IME      | GNOME/IBus and KDE/Fcitx5 compositor typing, Tab/Shift+Tab and Pinyin pass in containers. Physical devices remain separate. Changing an accessible text value does not test an IME.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| Scaling and targeting | The container adapters set/read actual Mutter/KScreen scales and verify compositor pointer hits at 100%, 150% and 200%. Standalone Xorg verifies XSettings 96/144/192 DPI, WebView pixel ratios and XTEST targeting. Visual caret/candidate placement remains separate. CSS zoom and an AT-SPI button action do not establish physical targeting.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| Notifications         | The container adapter activates the visible shell action and asserts the token, receiver PID and native focused-window result for live and cold launch. Calling the application's D-Bus callback directly would bypass the activation-token behavior under test.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| Accessibility         | Native roles/focus, Orca speech records, recorded audio and optional local ASR now work in GNOME and KDE. Native values, insertion/caret events and complete forward/reverse keyboard focus order also pass. Listening remains useful for announcement quality.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| Windows               | Use the [Windows UI Automation smoke](../../../tests/native/Runic.Desktop.WebViewSmoke/windows-ui-automation.md) through the existing interactive VM login. It verifies WebView2 accessibility names, editable focus, `ValuePattern`, `InvokePattern`, actual typed text and complete forward/reverse Tab navigation with native focused-element identity and `HasKeyboardFocus`, a UIA-point mouse click at the VM's observed 96 DPI, native open-file selection, open/save cancellation and atomic save with independent file verification, live output and deterministic process exit for JIT and executable-only NativeAOT publishes. Run native power-request checks independently. Session-0 SSH alone cannot cover interactive display behavior. Opt-in Narrator label/role speech with real WASAPI audio is implemented. Windows IME support is best effort; the strict Pinyin probe is disabled pending a later review of state-dependent WebView2 behavior. Physical input, visual candidate placement, independent audio transcription, display-scale changes/multi-DPI pointer behavior, overwrite-confirmation flows and visual rendering remain Windows work. |
| macOS                 | Add an AXUIElement/Accessibility adapter and native assertions after the real Mac is available. Keep native support explicitly untested until then.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |

Start these as focused, opt-in desktop jobs. Move stable scenarios into CI with
the same prepared dependencies and failure handling. Preserve
structured results, native logs and failure screenshots; do not add broad soaks,
mandatory manual gates or retry failures until they happen to pass. Visual IME
placement, spoken quality and real power transitions are still outside the
current automated runner's claims.
