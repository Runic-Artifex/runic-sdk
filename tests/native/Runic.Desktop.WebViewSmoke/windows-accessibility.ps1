# Opt-in Narrator checks, dot-sourced by the interactive UIA driver.
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RunicAccessibilityKeys {
 [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code,uint type);
 [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
'@
function Send-NativeChord([byte[]]$Keys) {
 try { foreach($key in $Keys) { [RunicAccessibilityKeys]::keybd_event($key,[byte][RunicAccessibilityKeys]::MapVirtualKey($key,0),[uint32]($key -eq 0x2D),[UIntPtr]::Zero) } }
 finally { for($i=$Keys.Length-1;$i -ge 0;$i--) { [RunicAccessibilityKeys]::keybd_event($Keys[$i],[byte][RunicAccessibilityKeys]::MapVirtualKey($Keys[$i],0),[uint32](2 + [int]($Keys[$i] -eq 0x2D)),[UIntPtr]::Zero) } }
}
function Test-RunicNarrator($Window, [string]$Output) {
 if(Get-Process Narrator -ErrorAction SilentlyContinue) { throw 'Leave existing Narrator sessions unchanged; stop Narrator before the opt-in test.' }
 Add-Type -Path "$PSScriptRoot/windows-loopback.cs"
 $clipboard = [System.Windows.Forms.Clipboard]::GetDataObject()
 $process=$null; $recording=$null; $results=@(); $started=Get-Date
 $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
 try {
  $process=Start-Process "$env:windir/System32/Narrator.exe" -PassThru
  Start-Sleep -Seconds 4
  $process = Get-Process Narrator | Where-Object { $_.SessionId -eq $session -and $_.StartTime -ge $started.AddSeconds(-1) } | Select-Object -First 1
  if(-not $process){throw 'Narrator did not start in the test session'}
  @{processId=$process.Id;startTicks=$process.StartTime.ToUniversalTime().Ticks;sessionId=$session}|ConvertTo-Json|Set-Content "$Output.narrator-running" -Encoding utf8
  foreach($id in @('display-name','record')) {
   $control=Find-Element $Window (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
   $control.SetFocus(); Wait-KeyboardFocus $control
   Start-Sleep -Seconds 2
   Send-NativeChord @(0x11)
   $recording=New-Object RunicLoopback("$Output.narrator-$id.wav")
   Send-NativeChord @(0x2D,0x09) # Narrator+Tab: read the actual focused item.
   Start-Sleep -Seconds 8
   $recording.Dispose(); $recording=$null
   [System.Windows.Forms.Clipboard]::Clear()
   Send-NativeChord @(0x2D,0x11,0x58) # Copy Narrator's last spoken phrase.
   $deadline=[DateTime]::UtcNow.AddSeconds(5); $spoken=''
   do { $spoken=[System.Windows.Forms.Clipboard]::GetText(); if($spoken){break}; Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $deadline)
   $spoken | Set-Content "$Output.narrator-$id.txt" -Encoding utf8
   if($spoken -notlike "*$($control.Current.Name)*") { throw "Narrator did not announce $id; speech: $spoken" }
   $role = if($id -eq 'display-name') {'(?i)\b(edit|Bearbeiten)\b'}else{'(?i)\b(button|Schaltfl.che)\b'}
   if($spoken -notmatch $role){throw "Narrator did not announce the $id role (English/German); speech: $spoken"}
   $results += [ordered]@{id=$id;name=$control.Current.Name;spoken=$spoken;audio="$Output.narrator-$id.wav"}
  }
  $results|ConvertTo-Json -Depth 4|Set-Content "$Output.narrator.json" -Encoding utf8
  return $results
 } finally {
  try { if($recording){$recording.Dispose()} }
  finally {
   try {
   if($process -and -not $process.HasExited) {
    Send-NativeChord @(0x2D,0x1B) # Narrator+Escape exits its UIAccess process.
    if(-not $process.WaitForExit(5000)){throw 'The test Narrator process did not exit; retained ownership marker for the task runner'}
   }
   Remove-Item "$Output.narrator-running" -ErrorAction SilentlyContinue
   } finally {
    if($clipboard){[System.Windows.Forms.Clipboard]::SetDataObject($clipboard,$true)}else{[System.Windows.Forms.Clipboard]::Clear()}
   }
  }
 }
}
