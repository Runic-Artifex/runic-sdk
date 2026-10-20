# WPF UI Automation check

`wpf-ui-automation.ps1` checks what a screen reader gets from a WPF window with an
embedded Runic web View. Narrator reads the UI Automation tree and follows its
focus events, so the script checks those. It drives the
[hybrid editor example](../../../examples/wpf-hybrid-editor/README.md) with UI
Automation and real key presses (`SendInput`):

- The shell window, the `RunicViewHost`'s web View pane (named by
  `AutomationProperties.Name` on the host) and the WebView2 document are in one
  UI Automation tree, with their names and control types. The document sits
  under the pane.
- The page's semantic tree is reachable under the document: the heading, the
  Title and Body inputs and the Save and Cancel save buttons. The Title input
  shows the model's title once the Bridge connects.
- Keyboard focus moves into and out of the web content. Tab goes from the
  Reload button to the document's first input, through it, and out to the WPF
  window's next control. Shift+Tab goes back the same way. After each key,
  `AutomationElement.FocusedElement` and keyboard focus are on the expected
  element, and a focus-changed event was raised for it.
- After Enter on Reload replaces the WebView, focus stays on the WPF button and
  the same Tab round works in the new document.
- With `-Narrator`, Narrator starts, the Tab round runs again while it reads,
  and the app keeps working. The script then checks Narrator's own spoken text,
  which it copies with Narrator+Ctrl+X, for the web input and for the WPF
  button that focus returns to. English and German role words are recognized.
  Narrator is stopped afterwards.

The script writes a JSON receipt with every check, `<receipt>.tree.txt` with the
window's UI Automation tree, and `<receipt>.narrator-*.txt` with the spoken text.
A failure writes `<receipt>.failure.json` and `<receipt>.failure-tree.txt`.

CI runs the check without Narrator on win-x64, in the WPF job (`ci.yml`, "Check
the hybrid WPF editor with UI Automation"). It runs the built app from
`examples/wpf-hybrid-editor/App/bin/Release/net10.0-windows`.

## Run it

It needs an unlocked interactive desktop and the WebView2 Runtime. Publish the
app, framework-dependent (the .NET 10 Desktop Runtime must be installed):

```sh
bun run bootstrap
bun run --cwd packages/web/views --bun build
dotnet publish examples/wpf-hybrid-editor/App/HybridNotes.Wpf.csproj -c Release -r win-x64 --self-contained false -o <dir>
```

From a desktop session (Windows PowerShell 5.1):

```powershell
./wpf-ui-automation.ps1 -Executable <dir>\HybridNotes.Wpf.exe -ReceiptPath C:\temp\wpf-uia\receipt.json [-Narrator]
```

SSH runs in Session 0, which has no desktop. When the test user is signed in,
[`run-windows-ui-automation.ps1`](../Runic.Desktop.WebViewSmoke/run-windows-ui-automation.ps1)
starts the script in that user's session through a temporary Scheduled Task, and
stops a Narrator it started if the run is interrupted:

```powershell
..\Runic.Desktop.WebViewSmoke\run-windows-ui-automation.ps1 `
  -Executable <dir>\HybridNotes.Wpf.exe `
  -AutomationScript .\wpf-ui-automation.ps1 `
  -ReceiptPath C:\temp\wpf-uia\receipt.json -Narrator
```

Don't run it while Narrator is already running (it refuses), and don't use the
keyboard or mouse during the run. CI runs without `-Narrator`; the Narrator part
runs on a Windows 11 test machine with a signed-in user.
