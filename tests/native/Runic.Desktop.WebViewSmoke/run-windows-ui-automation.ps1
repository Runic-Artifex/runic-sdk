[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$Executable,
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$AutomationScript,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [switch]$RequireExecutableOnly,
    [switch]$Ime,
    [switch]$Narrator,
    [ValidateRange(10, 240)][int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
# Deferred while Windows WebView2 composition remains state-dependent.
# Keep the parameter compatible with existing invocations, but never run the probe.
if ($Ime) {
    Write-Warning 'Windows IME testing is disabled; support is best effort. Continuing without IME coverage.'
    $Ime = $false
}
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$AutomationScript = (Resolve-Path -LiteralPath $AutomationScript).Path
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
if (Test-Path -LiteralPath $ReceiptPath) { throw "Receipt path already exists: $ReceiptPath" }
$runningPath = "$ReceiptPath.running"
if (Test-Path -LiteralPath $runningPath) { throw "Running marker already exists: $runningPath" }
if ($RequireExecutableOnly) {
    $deploymentFiles = Get-ChildItem -LiteralPath (Split-Path -Parent $Executable) -File -Recurse -Force
    if ($deploymentFiles.Count -ne 1 -or $deploymentFiles[0].FullName -ne $Executable) {
        throw 'The executable-only check requires the supplied EXE to be the only deployment file.'
    }
}

# OpenSSH service commands use Session 0. Registering an Interactive task under
# the already-signed-in test user makes the child use that user's real desktop.
# The task name is unique and removed in finally, so this leaves no registration.
$taskName = "Runic.Desktop.UIA.$([Guid]::NewGuid().ToString('N'))"
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Executable "{1}" -ReceiptPath "{2}"' -f `
    $AutomationScript, $Executable, $ReceiptPath
if ($Ime) { $arguments += ' -Ime' }
if ($Narrator) { $arguments += ' -Narrator' }
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument $arguments
$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited

try {
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
    $beforeStart = Get-ScheduledTaskInfo -TaskName $taskName
    $started = $false
    Start-ScheduledTask -TaskName $taskName
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $info = Get-ScheduledTaskInfo -TaskName $taskName
        $task = Get-ScheduledTask -TaskName $taskName
        if ($task.State -eq 'Running' -or $info.LastRunTime -gt $beforeStart.LastRunTime) { $started = $true }
        if ((Test-Path -LiteralPath $ReceiptPath) -and $task.State -ne 'Running' -and $info.LastTaskResult -eq 0) {
            $receipt = Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json
            if ($receipt.sessionId -eq 0) { throw 'The UI Automation receipt came from Session 0.' }
            Write-Output "PASS interactive Windows UI Automation task: $($receipt.windowName) / $($receipt.snapshotName)"
            return
        }
        # Task Scheduler reports 267009/267011 while a just-started task has no
        # completed result. Only a real nonzero completion is an early failure.
        if ($started -and $info.LastTaskResult -notin @(0, 267009, 267011)) {
            throw "Interactive UI Automation task failed with result $($info.LastTaskResult)."
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for interactive UI Automation receipt: $ReceiptPath"
}
finally {
    try {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath "$ReceiptPath.narrator-running") {
            $owned = Get-Content -LiteralPath "$ReceiptPath.narrator-running" -Raw | ConvertFrom-Json
            $narratorProcess = Get-Process -Id $owned.processId -ErrorAction SilentlyContinue
            if ($narratorProcess -and $narratorProcess.ProcessName -eq 'Narrator' -and
                $narratorProcess.SessionId -eq $owned.sessionId -and
                $narratorProcess.StartTime.ToUniversalTime().Ticks -eq $owned.startTicks) {
                Stop-Process -Id $narratorProcess.Id -Force
            }
            Remove-Item -LiteralPath "$ReceiptPath.narrator-running"
        }
        if (Test-Path -LiteralPath $runningPath) {
            try {
                $running = Get-Content -LiteralPath $runningPath -Raw | ConvertFrom-Json
                $candidate = Get-Process -Id $running.processId -ErrorAction SilentlyContinue
                if ($null -ne $candidate -and $running.executable -eq $Executable -and $candidate.Path -eq $Executable) {
                    Stop-Process -Id $candidate.Id -Force
                }
            }
            catch { Write-Warning "Could not inspect the test-owned running marker: $($_.Exception.Message)" }
        }
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}
