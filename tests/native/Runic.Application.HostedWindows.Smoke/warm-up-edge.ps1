# #1301: Edge's first run in a new profile, outside the default-browser smoke's timed phase. On its first run in the
# freshly created profile of runic-hosted, Edge wrote its Local State and exited without opening the smoke's URL (run
# 45668620089), so the smoke waited until its deadline. The CI step runs this script as that user (--run-unelevated)
# before the smoke: it starts Edge with about:blank, waits until the profile's Local State exists, and closes Edge again.
# Bounded and logged; it never fails the step, so the smoke's own phase logging still shows any other cause.
$ErrorActionPreference = 'Stop'
$started = [Diagnostics.Stopwatch]::StartNew()
function Log([string] $message) { Write-Output ("[edge warm-up {0:0.0} s] {1}" -f $started.Elapsed.TotalSeconds, $message) }

try {
  $state = Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data\Local State'
  Log "user $env:USERNAME; Local State $(if (Test-Path $state) { 'exists' } else { 'missing' }) at $state"
  # Edge's App Paths entry, as ShellExecuteEx resolves msedge.
  $edge = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe' -ErrorAction SilentlyContinue).'(default)'
  if (-not $edge -or -not (Test-Path $edge)) {
    Log 'Edge is not installed; nothing to warm up.'
    exit 0
  }
  $policies = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -ErrorAction SilentlyContinue
  Log "Edge $edge; HideFirstRunExperience $($policies.HideFirstRunExperience)"
  $process = Start-Process -FilePath $edge -ArgumentList 'about:blank' -PassThru
  Log "started Edge $($process.Id) with about:blank"
  $deadline = [TimeSpan]::FromSeconds(60)
  while (-not (Test-Path $state) -and $started.Elapsed -lt $deadline) { Start-Sleep -Milliseconds 500 }
  if (Test-Path $state) { Log 'Local State exists' } else { Log "Local State still missing after $($deadline.TotalSeconds) s" }
  # Lets Edge finish writing its profile before it is closed.
  Start-Sleep -Seconds 5
  $running = @(Get-Process msedge -ErrorAction SilentlyContinue)
  Log "$($running.Count) msedge processes visible to this user; $(@($running | Where-Object MainWindowHandle -ne 0).Count) with a window"
  # Closes Edge's windows, as a user would, so its next start is no crash recovery; then ends what is left of this user's.
  foreach ($browser in $running) { try { [void] $browser.CloseMainWindow() } catch { } }
  $closing = [Diagnostics.Stopwatch]::StartNew()
  while (@(Get-Process msedge -ErrorAction SilentlyContinue).Count -gt 0 -and $closing.Elapsed.TotalSeconds -lt 15) { Start-Sleep -Milliseconds 500 }
  $left = @(Get-Process msedge -ErrorAction SilentlyContinue)
  if ($left.Count -gt 0) {
    Log "$($left.Count) msedge processes still running; ending them"
    $left | Stop-Process -Force -ErrorAction SilentlyContinue
  }
  Log "done; Local State $(if (Test-Path $state) { 'exists' } else { 'missing' })"
}
catch {
  Log "warm-up failed: $_"
}
exit 0
