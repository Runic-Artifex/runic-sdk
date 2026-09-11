# Windows UI Automation smoke

`windows-ui-automation.ps1` must run in the user's interactive Windows session.
It starts the supplied JIT or NativeAOT `Runic.Desktop.WebViewSmoke.exe` with
`--ui-automation`, finds its WebView2 controls through UI Automation, writes the
edit value with `ValuePattern`, verifies the initial native edit focus, invokes
controls with `InvokePattern`, and requires the resulting live output to appear
in the native accessibility tree. It then invokes a visible Finish button, so
the fixture only exits after the automation client has observed the output.

```powershell
./windows-ui-automation.ps1 `
  -Executable C:\path\to\Runic.Desktop.WebViewSmoke.exe `
  -ReceiptPath C:\path\to\uia-receipt.json
```

SSH runs in Session 0 and cannot observe desktop UI. When the test user is
already signed in, `run-windows-ui-automation.ps1` starts the driver through a
unique Scheduled Task with an `Interactive` logon type, waits for the receipt,
and removes that task. It can therefore be called from Session 0 without
pretending that Session 0 itself has a desktop:

```powershell
./run-windows-ui-automation.ps1 `
  -Executable C:\path\to\Runic.Desktop.WebViewSmoke.exe `
  -AutomationScript .\windows-ui-automation.ps1 `
  -ReceiptPath C:\path\to\uia-receipt.json
```

The scripts do not install software. Optional Narrator checks temporarily select
input profiles and start Narrator;
normal completion and failure restore the original language list/profile and clipboard. They create
and remove receipt-specific input/output files. The native open-file dialog
returns a real `IReadFileLease`; open and save cancellation must return `Dismissed`.
The save path acquires an `ISaveFileLease`, stages a `RequireAtomicReplace` write,
and commits it. The driver independently verifies the saved bytes and unchanged
input file. Both open/save selectors use native control IDs, including the save
dialog's `FileNameControlHost` and child `Edit`, rather than localized labels. The pointer check uses a UIA
clickable point for the named target rather than a hard-coded screen coordinate.
Add `-RequireExecutableOnly` for NativeAOT. The task runner then requires the
executable to be the only file in its directory. Copying only the NativeAOT
`.exe` to an empty test directory therefore proves that its linked loader can
start the installed WebView2 runtime. A JIT publish uses its packaged loader and
runs without that switch.

This smoke covers semantic WebView2 accessibility, programmatic editable focus,
value and invoke patterns, actual `SendKeys` text entry, Tab/Shift+Tab/Enter
behavior, a UIA-point mouse click, native open-file selection, open/save dismissal,
atomic save, live output, and clean fixture lifecycle. Forward and reverse Tab
navigation covers every form control and requires both `HasKeyboardFocus` and
matching `AutomationElement.FocusedElement` identity. An accessible DOM focus
output supplements these native checks. Escaped text is submitted as one keyboard
sequence and consumed before navigation; the earlier button-focus limitation no
longer reproduces with this sequencing.

The driver records the native host's current effective DPI, but has only passed
at the VM's observed 96 DPI. Physical keyboard devices, candidate-popup placement,
independent speech recognition, multi-DPI/display changes, overwrite-confirmation flows,
and visual rendering remain outside this smoke. Native inhibition is covered separately: the
maintained `--system-only` test runs from SSH, while full display and system
acquisition requires an interactive desktop; `powercfg /requests` observation
also requires elevation.

## Microsoft Pinyin and Narrator

**Windows IME support is best effort. The strict Pinyin probe is disabled.**
Passing `-Ime` emits a warning and continues the other checks without IME coverage.
WebView2 can commit Chinese text while showing detached composition and omitting
DOM composition events; the behavior also occurs in a standard WebView2 host,
while Edge passed the same page. Successful runs also occur, and no reliable
Runic-specific fix has been identified. Revisit in December 2026 or later with
updated Windows/WebView2 versions; this is not a release blocker.

Add `-Narrator` to either command above. Narrator can run independently. Run in an unlocked, dedicated test
session with an English (US/UK) or German keyboard, no existing Narrator process,
and a working default audio output. Keep the helper files `windows-accessibility.ps1`
and `windows-loopback.cs` alongside the driver. Narrator role assertions currently
recognize English and German speech; another locale needs equivalent role words.

The disabled diagnostic implementation is retained in `windows-accessibility.ps1`
for future investigation. It does not run through either supported entry point.

Narrator runs after keyboard and picker checks. The driver focuses the edit
and button, requests Narrator's read-current-item command, and checks the labels
and roles in Narrator's own copied speech text. A test-only WASAPI helper records
the default render endpoint into PCM WAV files and rejects silence or less than
one second of audio. It never opens a microphone. These checks establish native
speech text and non-silent output; they do not independently transcribe the WAVs
or judge pronunciation. Narrator's exit shortcut is followed by a process-exit
check. The task runner can also terminate the exact owned Narrator PID/start time
if the driver is interrupted. Existing Narrator sessions are rejected untouched.

Receipts have an `accessibility` section; sibling `.narrator.json`,
`.narrator-*.txt` and `.narrator-*.wav` files retain diagnostics. A hard process kill
cannot execute the driver's language/clipboard restoration; use the dedicated
test account and restore its original input settings after such an interruption.
The normal bounded error paths run restoration. Do not run interactive suites
concurrently in the same desktop.
