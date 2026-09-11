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

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-Element([System.Windows.Automation.AutomationElement]$Root, [System.Windows.Automation.Condition]$Condition, [int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $found = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $Condition)
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "UI Automation element was not available: $Condition"
}

$process = $null
try {
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    if ($sessionId -eq 0) { throw 'Windows UI Automation requires an interactive session, not Session 0.' }
    $process = Start-Process -FilePath $Executable -ArgumentList '--ui-automation' -PassThru
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
    $focusDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        if ($edit.Current.HasKeyboardFocus) { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $focusDeadline)
    if (-not $edit.Current.HasKeyboardFocus) { throw 'Display name did not receive keyboard focus.' }
    $value = [System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    if ($value.Current.IsReadOnly) { throw 'Display name is unexpectedly read-only.' }
    $value.SetValue('UI Automation value')

    $buttonCondition = New-Object System.Windows.Automation.AndCondition @(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Record snapshot')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $button = Find-Element $window $buttonCondition
    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()

    $snapshotCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Recorded: UI Automation value')
    # The output is the round-trip assertion. Some WebView2 versions deliver a
    # ValuePattern property change asynchronously, while this DOM result proves
    # the provider's SetValue reached the actual input before Invoke ran.
    $snapshot = Find-Element $window $snapshotCondition
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
    $finishName = $finish.Current.Name
    ([System.Windows.Automation.InvokePattern]$finish.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Wait-Process -Id $process.Id -Timeout 20
    if ($process.ExitCode -ne 0) { throw "UI Automation fixture exited with $($process.ExitCode)." }
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
}
