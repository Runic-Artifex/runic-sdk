# Windows UI Automation smoke

`windows-ui-automation.ps1` must run in the user's interactive Windows session.
It starts the supplied JIT or NativeAOT `Runic.Desktop.WebViewSmoke.exe` with
`--ui-automation`, finds its WebView2 controls through UI Automation, writes the
edit value with `ValuePattern`, verifies keyboard focus, invokes the button with
`InvokePattern`, and requires the resulting live output to appear in the native
accessibility tree. It then invokes a visible Finish button, so the fixture only
exits after the automation client has observed the output.

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

The scripts do not install software, change accessibility settings, or use
coordinate input. Add `-RequireExecutableOnly` for NativeAOT. The task runner
then requires the executable to be the only file in its directory. Copying only
the NativeAOT `.exe` to an empty test directory therefore proves that its linked
loader can start the installed WebView2 runtime. A JIT publish uses its packaged
loader and runs without that switch.

This smoke covers semantic WebView2 accessibility, programmatic editable focus,
value and invoke patterns, live output, and clean fixture lifecycle. It does not
exercise physical keyboard navigation or IME composition, DPI/scaling or pointer
targeting, Narrator/speech output, native picker UI, or visual rendering. Native
inhibition is covered separately: the maintained `--system-only` test runs from
SSH, while full display and system acquisition requires an interactive desktop;
`powercfg /requests` observation also requires elevation.
