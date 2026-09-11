[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$Executable,
    [Parameter(Mandatory)][string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ReceiptPath)) | Out-Null
if (Test-Path -LiteralPath $ReceiptPath) { throw "Receipt path already exists: $ReceiptPath" }
$runningPath = "$ReceiptPath.running"
if (Test-Path -LiteralPath $runningPath) { throw "Running marker already exists: $runningPath" }
$pickerContents = 'Runic Windows native picker input.'
$PickerPath = Join-Path ([IO.Path]::GetDirectoryName($ReceiptPath)) 'runic-uia-picker-input.txt'
if (Test-Path -LiteralPath $PickerPath) { throw "Picker input path already exists: $PickerPath" }
Set-Content -LiteralPath $PickerPath -Value $pickerContents -NoNewline -Encoding utf8
$PickerPath = (Resolve-Path -LiteralPath $PickerPath).Path

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RunicWindowsInput {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError=true)] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
}
'@

function Find-Element([System.Windows.Automation.AutomationElement]$Root, [System.Windows.Automation.Condition]$Condition, [int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $found = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $Condition)
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "UI Automation element was not available: $Condition"
}

function Wait-KeyboardFocus([System.Windows.Automation.AutomationElement]$Element, [int]$TimeoutSeconds = 5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Element.Current.HasKeyboardFocus) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "UI Automation element did not receive keyboard focus: $($Element.Current.Name)"
}

function Wait-Value([System.Windows.Automation.ValuePattern]$Pattern, [string]$Expected, [int]$TimeoutSeconds = 5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Pattern.Current.Value -eq $Expected) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Editable value did not become '$Expected'."
}

function Wait-DomFocus([System.Windows.Automation.AutomationElement]$Window, [string]$Id) {
    return Find-Element $Window (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "Keyboard focus: $Id")) 5
}

function Send-LiteralKeys([object]$Keyboard, [string]$Text) {
    foreach ($character in $Text.ToCharArray()) {
        $keys = switch ($character) {
            '+' { '{+}' }
            '^' { '{^}' }
            '%' { '{%}' }
            '~' { '{~}' }
            '(' { '{(}' }
            ')' { '{)}' }
            '[' { '{[}' }
            ']' { '{]}' }
            '{' { '{{}' }
            '}' { '{}}' }
            default { [string]$character }
        }
        $Keyboard.SendKeys($keys)
    }
}

function Find-ActiveDialog([System.Windows.Automation.AutomationElement]$MainWindow, [int]$TimeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $fileNameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1148')
    do {
        # Common Item Dialog is an owned child window on this host, rather than
        # a top-level desktop child. Its stable file-name control ID survives
        # the localized dialog caption and button names.
        $windows = $MainWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, $windowCondition)
        foreach ($candidate in $windows) {
            if ($null -ne $candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $fileNameCondition)) {
                return $candidate
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The native file picker did not expose its file-name control.'
}

$process = $null
try {
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    if ($sessionId -eq 0) { throw 'Windows UI Automation requires an interactive session, not Session 0.' }
    $process = Start-Process -FilePath $Executable -ArgumentList '--ui-automation' -PassThru -RedirectStandardOutput "$ReceiptPath.stdout" -RedirectStandardError "$ReceiptPath.stderr"
    # Process.Handle is no longer available from the PowerShell process wrapper
    # after the child exits. Keep the OS handle while it is live and use it only
    # after WaitForExit has established a completed process.
    [IntPtr]$processHandle = $process.Handle
    [ordered]@{ processId = $process.Id; sessionId = $sessionId; executable = $Executable } |
        ConvertTo-Json | Set-Content -LiteralPath $runningPath -Encoding utf8
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $window = $null
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $processCondition)
        # WebView2 first creates its native host as "Loading..." and changes the
        # caption after the document title event arrives.
        if ($null -ne $candidate -and $candidate.Current.Name -eq 'Runic Desktop UI Automation') {
            $window = $candidate
            break
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $windowDeadline)
    if ($null -eq $window) { throw 'The native WebView2 window did not publish its document title.' }

    $editCondition = New-Object System.Windows.Automation.AndCondition @(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Display name')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    $edit = Find-Element $window $editCondition
    $edit.SetFocus()
    Wait-KeyboardFocus $edit
    $value = [System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    if ($value.Current.IsReadOnly) { throw 'Display name is unexpectedly read-only.' }
    $value.SetValue('UI Automation value')
    Wait-Value $value 'UI Automation value'

    # SendKeys drives the interactive desktop's focused-control keyboard route;
    # this is intentionally separate from UIA ValuePattern above.
    $keyboard = New-Object -ComObject WScript.Shell
    $keyboard.SendKeys('^a')
    Send-LiteralKeys $keyboard 'Keyboard value'
    # Do not send Tab until WebView2 has consumed the text events. Sending both
    # at once can race its native input queue and leave focus on the edit.
    Wait-Value $value 'Keyboard value'
    $keyboard.SendKeys('{TAB}')

    $buttonCondition = New-Object System.Windows.Automation.AndCondition @(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Record snapshot')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $button = Find-Element $window $buttonCondition
    $recordFocus = Wait-DomFocus $window 'record'
    $keyboard.SendKeys('+{TAB}')
    $editFocus = Wait-DomFocus $window 'display-name'
    $keyboard.SendKeys('{TAB}')
    $recordFocusAgain = Wait-DomFocus $window 'record'
    $keyboard.SendKeys('{ENTER}')

    $snapshotCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Recorded: Keyboard value')
    # The output is the round-trip assertion. It proves actual keyboard text and
    # Enter activation reached the WebView before a UIA result is accepted.
    $snapshot = Find-Element $window $snapshotCondition

    $pointerCondition = New-Object System.Windows.Automation.AndCondition @(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'pointer-target')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $pointerTarget = Find-Element $window $pointerCondition
    if ($pointerTarget.Current.Name -ne 'Pointer target: 0') { throw "Pointer target had unexpected initial state: $($pointerTarget.Current.Name)" }
    $point = $pointerTarget.GetClickablePoint()
    $bounds = $pointerTarget.Current.BoundingRectangle
    if ($point.X -lt $bounds.Left -or $point.X -gt $bounds.Right -or $point.Y -lt $bounds.Top -or $point.Y -gt $bounds.Bottom) {
        throw 'The UIA clickable point fell outside the pointer target bounds.'
    }
    $effectiveDpi = [RunicWindowsInput]::GetDpiForWindow([IntPtr]$window.Current.NativeWindowHandle)
    if ($effectiveDpi -le 0) { throw 'Windows did not report an effective DPI for the native host window.' }
    if (-not [RunicWindowsInput]::SetCursorPos([int][Math]::Round($point.X), [int][Math]::Round($point.Y))) { throw 'SetCursorPos failed for the UIA target point.' }
    [RunicWindowsInput]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [RunicWindowsInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    $pointerResult = Find-Element $window (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Pointer target: 1'))

    $pickerButtonCondition = New-Object System.Windows.Automation.AndCondition @(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Open native file')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $pickerButton = Find-Element $window $pickerButtonCondition
    ([System.Windows.Automation.InvokePattern]$pickerButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $pickerWindow = Find-ActiveDialog $window
    $pickerFileNameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1148')
    $pickerFileHost = $null
    foreach ($candidate in $pickerWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pickerFileNameCondition)) {
        if ($candidate.Current.NativeWindowHandle -ne 0) { $pickerFileHost = $candidate }
    }
    if ($null -eq $pickerFileHost) { throw 'The native picker did not expose a focusable file-name HWND.' }
    # Capture dialog/provider data before Enter can close the Common Item Dialog.
    $pickerWindowName = $pickerWindow.Current.Name
    $pickerWindowClass = $pickerWindow.Current.ClassName
    $pickerFileNameId = $pickerFileHost.Current.AutomationId
    $pickerFileNameHandle = $pickerFileHost.Current.NativeWindowHandle
    # Common Item Dialog exposes its file-name combo as an opaque UIA Pane on
    # Windows 11. Focus its verified native child HWND, then use keyboard input.
    [RunicWindowsInput]::SetFocus([IntPtr]$pickerFileHost.Current.NativeWindowHandle) | Out-Null
    $keyboard.SendKeys('^a')
    Send-LiteralKeys $keyboard $PickerPath
    $keyboard.SendKeys('{ENTER}')
    $pickerResult = Find-Element $window (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "Picked: $([IO.Path]::GetFileName($PickerPath)): $pickerContents"))
    $finishCondition = New-Object System.Windows.Automation.AndCondition @(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Finish')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $finish = Find-Element $window $finishCondition
    # Capture provider data while its HWND is still alive. Reading Current after
    # Finish has closed the fixture can yield null properties on Windows UIA.
    $windowName = $window.Current.Name
    $editName = $edit.Current.Name
    $editControlType = $edit.Current.ControlType.ProgrammaticName
    $buttonName = $button.Current.Name
    $buttonControlType = $button.Current.ControlType.ProgrammaticName
    $snapshotName = $snapshot.Current.Name
    $pointerName = $pointerResult.Current.Name
    $pointerX = $point.X
    $pointerY = $point.Y
    $pickerResultName = $pickerResult.Current.Name
    $finishName = $finish.Current.Name
    ([System.Windows.Automation.InvokePattern]$finish.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    if (-not $process.WaitForExit(20000)) { throw 'UI Automation fixture did not exit after Finish.' }
    [uint32]$exitCode = 0
    if (-not [RunicWindowsInput]::GetExitCodeProcess($processHandle, [ref]$exitCode)) { throw 'Could not read the UI Automation fixture exit code.' }
    if ($exitCode -ne 0) { throw "UI Automation fixture exited with $exitCode." }
    $receipt = [ordered]@{
        executable = $Executable
        processId = $process.Id
        sessionId = $sessionId
        windowName = $windowName
        editName = $editName
        editControlType = $editControlType
        buttonName = $buttonName
        buttonControlType = $buttonControlType
        snapshotName = $snapshotName
        pointerName = $pointerName
        effectiveDpi = $effectiveDpi
        pointerX = $pointerX
        pointerY = $pointerY
        pickerWindowName = $pickerWindowName
        pickerWindowClass = $pickerWindowClass
        pickerFileNameId = $pickerFileNameId
        pickerFileNameHandle = $pickerFileNameHandle
        pickerResultName = $pickerResultName
        finishName = $finishName
        completedUtc = [DateTime]::UtcNow.ToString('o')
    }
    # Windows PowerShell 5.1 is present on a clean Windows install and accepts
    # UTF8 (with a BOM), while utf8NoBOM is a PowerShell 6+ encoding name.
    $receipt | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ReceiptPath -Encoding utf8
    Write-Output "PASS Windows UI Automation: WebView2 exposes semantic edit/button/output controls and accepts Value/Invoke patterns."
}
catch {
    # Scheduled tasks do not return child stderr to their Session 0 caller. Keep
    # a sibling diagnostic so an interactive failure stays actionable.
    $_ | Format-List * -Force | Out-String | Set-Content -LiteralPath "$ReceiptPath.log" -Encoding utf8
    throw
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item -LiteralPath $runningPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PickerPath -Force -ErrorAction SilentlyContinue
}
