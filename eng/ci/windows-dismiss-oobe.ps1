# The windows-11-arm image starts its desktop session with Windows' full-screen "Microsoft account" prompt
# (WWAHost.exe, with Explorer's Shell_OOBEProxy window) above every window. While it is up, Explorer refuses
# Shell_NotifyIcon(NIM_ADD) with E_FAIL (#373) and DoDragDrop blocks before its first QueryContinueDrag (#391), also
# without Runic. Closing the prompt gives the session a usable desktop. Only WWAHost is stopped: the runner's own agent
# has a console window on the same desktop. Where the prompt is absent (windows-2025 x64) this only logs.
$ErrorActionPreference = 'Stop'
Add-Type -Namespace RunicCi -Name Desktop -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string name);
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(long point);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
[DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
'@

function Get-CentreWindow {
    $x = [RunicCi.Desktop]::GetSystemMetrics(0) / 2
    $y = [RunicCi.Desktop]::GetSystemMetrics(1) / 2
    $window = [RunicCi.Desktop]::WindowFromPoint(([long]$y -shl 32) -bor [long]$x)
    $name = [System.Text.StringBuilder]::new(256)
    [void][RunicCi.Desktop]::GetClassName($window, $name, $name.Capacity)
    $owner = 0
    [void][RunicCi.Desktop]::GetWindowThreadProcessId($window, [ref]$owner)
    $process = (Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName
    [pscustomobject]@{ Class = $name.ToString(); Process = $process; Text = "'$name' of $process ($owner)" }
}

"Window at the screen centre: $((Get-CentreWindow).Text)"
$prompt = Get-Process WWAHost -ErrorAction SilentlyContinue
if ([RunicCi.Desktop]::FindWindow('Shell_OOBEProxy', $null) -eq [IntPtr]::Zero -and -not $prompt) {
    'No Microsoft account prompt on this desktop.'
    exit 0
}
foreach ($process in $prompt) {
    "Stopping the Microsoft account prompt: WWAHost $($process.Id) '$($process.MainWindowTitle)'"
    Stop-Process -Id $process.Id -Force
}
$deadline = [DateTime]::UtcNow.AddSeconds(15)
while ([RunicCi.Desktop]::FindWindow('Shell_OOBEProxy', $null) -ne [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 250
}
# The shell opens Start or Search once the prompt is gone; Escape closes the flyout.
for ($attempt = 0; $attempt -lt 3; $attempt++) {
    Start-Sleep -Seconds 1
    $centre = Get-CentreWindow
    if ($centre.Process -notin 'StartMenuExperienceHost', 'SearchHost', 'WWAHost') { break }
    "Closing $($centre.Text) with Escape"
    [RunicCi.Desktop]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [RunicCi.Desktop]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
}
"Shell_OOBEProxy present: $([RunicCi.Desktop]::FindWindow('Shell_OOBEProxy', $null) -ne [IntPtr]::Zero)"
"Window at the screen centre: $((Get-CentreWindow).Text)"
