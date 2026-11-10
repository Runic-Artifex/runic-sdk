# Prints what Windows recorded about a crashed process (eng/ci/windows-native-run.mjs): Windows Error Reporting's and
# the .NET runtime's Application event-log entries since the process started, the WER reports it filed, and the
# faulting thread's stack from the full dump WER wrote (LocalDumps) when the runner has the Windows debugger.
# Usage: windows-crash-report.ps1 -Since <ISO 8601 time> -ProcessId <id> -Image <name.exe> -DumpFolder <directory>
param(
  [Parameter(Mandatory)] [string] $Since,
  [Parameter(Mandatory)] [int] $ProcessId,
  [Parameter(Mandatory)] [string] $Image,
  [Parameter(Mandatory)] [string] $DumpFolder
)
$ErrorActionPreference = 'Continue'
$start = [DateTime]::Parse($Since).ToLocalTime().AddSeconds(-1)
$providers = 'Application Error', 'Windows Error Reporting', '.NET Runtime', 'Application Hang'

$wer = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' -ErrorAction SilentlyContinue
$service = Get-Service WerSvc -ErrorAction SilentlyContinue
Write-Output "Windows Error Reporting: Disabled=$($wer.Disabled), DontShowUI=$($wer.DontShowUI), WerSvc $($service.Status) ($($service.StartType))"

# WerFault writes the event and the dump after the process has gone; wait for them.
$events = @()
for ($waited = 0; $waited -lt 60; $waited += 2) {
  $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $start } -ErrorAction SilentlyContinue |
    Where-Object { $providers -contains $_.ProviderName })
  $dumps = @(Get-ChildItem $DumpFolder -Filter "$Image.$ProcessId.dmp" -ErrorAction SilentlyContinue)
  if ($dumps.Count -gt 0 -and ($events | Where-Object Id -eq 1001)) { break }
  Start-Sleep -Seconds 2
}

Write-Output "::group::Application event log since $($start.ToString('o')): $($events.Count) entries"
foreach ($entry in ($events | Sort-Object TimeCreated)) {
  Write-Output "--- $($entry.TimeCreated.ToString('o')) $($entry.ProviderName) $($entry.Id)"
  Write-Output $entry.Message
}
Write-Output '::endgroup::'

foreach ($store in 'ReportArchive', 'ReportQueue') {
  $folder = Join-Path $env:ProgramData "Microsoft\Windows\WER\$store"
  foreach ($report in (Get-ChildItem $folder -Directory -ErrorAction SilentlyContinue | Where-Object LastWriteTime -ge $start)) {
    Write-Output "::group::WER $store\$($report.Name)"
    $file = Join-Path $report.FullName 'Report.wer'
    if (Test-Path $file) {
      Get-Content $file | Where-Object { $_ -match '^(EventType|AppPath|Sig\[|DynamicSig\[|UI\[|FriendlyEventName|ReportDescription)' }
    }
    Get-ChildItem $report.FullName | ForEach-Object { "  $($_.Name) $($_.Length)" }
    Write-Output '::endgroup::'
  }
}

$dumps = @(Get-ChildItem $DumpFolder -Filter '*.dmp' -ErrorAction SilentlyContinue | Where-Object LastWriteTime -ge $start)
Write-Output "Minidumps in ${DumpFolder}: $(($dumps | ForEach-Object { "$($_.Name) ($($_.Length) bytes)" }) -join ', ')"
$architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$debugger = @((Get-Command cdb.exe -ErrorAction SilentlyContinue).Source,
  "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\$architecture\cdb.exe",
  "$env:ProgramFiles\Windows Kits\10\Debuggers\$architecture\cdb.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
$dump = $dumps | Where-Object Name -eq "$Image.$ProcessId.dmp" | Select-Object -First 1
if (-not $dump) { $dump = $dumps | Select-Object -First 1 }
if ($dump -and $debugger) {
  $env:_NT_SYMBOL_PATH = "srv*$env:RUNNER_TEMP\symbols*https://msdl.microsoft.com/download/symbols"
  # Each command on a line of its own in a script file, so that one the dump cannot answer leaves the others running.
  function Invoke-Debugger([string] $Title, [string[]] $Commands, [int] $Seconds = 150) {
    Write-Output "::group::${debugger}: $Title in $($dump.Name)"
    $script = Join-Path $env:RUNNER_TEMP "cdb-$([Guid]::NewGuid()).txt"
    Set-Content $script ($Commands + 'q')
    $process = Start-Process $debugger -ArgumentList '-z', "`"$($dump.FullName)`"", '-cf', "`"$script`"" -NoNewWindow -PassThru
    if (-not $process.WaitForExit($Seconds * 1000)) { $process.Kill(); Write-Output "The debugger did not finish in $Seconds s." }
    Write-Output '::endgroup::'
  }
  Invoke-Debugger "the exception and the faulting thread's stack" '.lastevent', '.exr -1', '.ecxr', 'kn 60', '~*kn 25'
} elseif ($dump) {
  Write-Output 'No cdb.exe on this runner: the dump is in the job artifact native-crash-dumps-<rid>-<run id>.'
}
