# Opt-in real input/speech checks, dot-sourced by the interactive UIA driver.
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RunicAccessibilityKeys {
 [StructLayout(LayoutKind.Sequential)] struct GuiInfo { public uint size, flags; public IntPtr active,focus,capture,menu,move,caret; public int left,top,right,bottom; }
 [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,System.Text.StringBuilder name,int count);
 public static string FocusInfo() { var info=new GuiInfo(); info.size=(uint)Marshal.SizeOf(info); if(!GetGUIThreadInfo(0,ref info)) return "No foreground focus"; var name=new System.Text.StringBuilder(256);GetClassName(info.focus,name,256);return info.focus.ToString("X")+" "+name; }

 [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code,uint type);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
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
Add-Type -ReferencedAssemblies System.Windows.Forms @'
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)] public struct RunicProfile {
 public uint Type; public ushort Language; public Guid Class, Profile, Category; public IntPtr Substitute; public uint Caps; public IntPtr Layout; public uint Flags;
}
[ComImport,Guid("71c6e74c-0f28-11d8-a82a-00065b84435c"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IRunicProfiles {
 [PreserveSig] int Activate(uint type, ushort language, ref Guid clsid, ref Guid profile, IntPtr layout, uint flags);
 void Deactivate(uint type, ushort language, ref Guid clsid, ref Guid profile, IntPtr layout, uint flags);
 void GetProfile(uint type, ushort language, ref Guid clsid, ref Guid profile, IntPtr layout, out RunicProfile result);
 void Enum(ushort language,out IntPtr result); void Release(ref Guid clsid,uint flags);
 void Register(ref Guid clsid,ushort language,ref Guid profile,IntPtr description,uint chars,IntPtr icon,uint iconChars,uint iconIndex,IntPtr substitute,uint layout,bool enabled,uint flags);
 void Unregister(ref Guid clsid,ushort language,ref Guid profile,uint flags);
 void GetActive(ref Guid category,out RunicProfile profile);
}
public sealed class RunicProfiles : IDisposable {
 IRunicProfiles manager=(IRunicProfiles)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("33C53A50-F456-4884-B049-85FD643ECFED")));
 public RunicProfile Current() { Guid category=new Guid("34745C63-B2F0-4784-8B67-5E12C8701A31"); RunicProfile p; manager.GetActive(ref category,out p); return p; }
 public void Activate(RunicProfile p) { int hr=manager.Activate(p.Type,p.Language,ref p.Class,ref p.Profile,p.Layout,0x20000000); if(hr!=0) throw new InvalidOperationException("Profile activation failed: "+hr.ToString("X8")); for(int i=0;i<20;i++){System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(25);} }
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
 [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
 public void ResetKeyboard() {
  var layouts=new IntPtr[GetKeyboardLayoutList(0,null)]; GetKeyboardLayoutList(layouts.Length,layouts);
  foreach(var layout in layouts) { if((layout.ToInt64() & 0xffff) == 0x0409 || (layout.ToInt64() & 0xffff) == 0x0809 || (layout.ToInt64() & 0xffff) == 0x0407) { Activate(new RunicProfile {Type=2,Language=(ushort)(layout.ToInt64() & 0xffff),Layout=layout}); PostMessage(GetForegroundWindow(),0x50,IntPtr.Zero,layout); return; } }
  throw new InvalidOperationException("Install a US/UK English or German keyboard for the desktop input test");
 }
 public void Pinyin() { Activate(new RunicProfile {Type=1,Language=0x0804,Class=new Guid("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E"),Profile=new Guid("FA550B04-5AD7-411F-A5AC-CA038EC515D7")}); }
 public void Dispose(){Marshal.ReleaseComObject(manager);}
}
'@
function Test-RunicIme($Window,$Edit,[string]$Output) {
 $Edit.SetFocus(); Wait-KeyboardFocus $Edit
 $profiles=New-Object RunicProfiles
 $previous=$profiles.Current()
 try {
  $value=[System.Windows.Automation.ValuePattern]$Edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  Wait-Value $value '' # The IME test starts in the untouched, empty field.
  $Edit.SetFocus(); Wait-KeyboardFocus $Edit
  if(-not [RunicAccessibilityKeys]::SetForegroundWindow([IntPtr]$Window.Current.NativeWindowHandle)){throw 'Cannot foreground IME fixture'}
  $profiles.ResetKeyboard()
  Start-Sleep -Milliseconds 500
  $profiles.Pinyin()
  Start-Sleep -Milliseconds 500
  if($profiles.Current().Language -ne 0x0804){throw 'Microsoft Pinyin did not become active'}
  $point=$Edit.GetClickablePoint()
  if(-not [RunicWindowsInput]::SetCursorPos([int]$point.X,[int]$point.Y)){throw 'Cannot position IME input pointer'}
  [RunicWindowsInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
  [RunicWindowsInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 500
  foreach($key in @(0x4E,0x49,0x48,0x41,0x4F)) { Send-NativeChord @($key); Start-Sleep -Milliseconds 150 }
  [RunicAccessibilityKeys]::FocusInfo() | Set-Content "$Output.ime-focus.txt"
  Start-Sleep -Milliseconds 500
  Send-NativeChord @(0x20)
  $expected=[string][char]0x4f60+[char]0x597d
  Wait-Value $value $expected
  Send-NativeChord @(0x09)
  $null=Wait-DomFocus $Window 'record'
  Wait-Value $value $expected
  $result=Find-Element $Window (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'ime-result'))
  $deadline=[DateTime]::UtcNow.AddSeconds(5)
  do {
   $text=$result.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text))).Current.Name
   $events=$text.Substring(5)|ConvertFrom-Json
   if(@($events|Where-Object {$_.type -eq 'compositionend'}).Count){break}
   Start-Sleep -Milliseconds 100
  } while([DateTime]::UtcNow -lt $deadline)
  foreach($type in @('compositionstart','compositionupdate')) {
   if(-not @($events|Where-Object {$_.type -eq $type -and $_.trusted}).Count){throw "Missing trusted $type event"}
  }
  # Chromium dispatches compositionend through ScopedEventQueue; this runtime
  # reports isTrusted=false there. Require trusted preedit/input and exact commit.
  if(-not @($events|Where-Object {$_.type -eq 'input' -and $_.trusted -and $_.composing}).Count){throw 'Missing native composing input'}
  $commits=@($events|Where-Object {$_.type -eq 'compositionend'})
  if($commits.Count -ne 1){throw 'Expected exactly one composition commit'}
  $commit=$commits[0]
  if($commit.data -cne $expected){throw 'Pinyin composition did not commit the expected Chinese text'}
  $events|ConvertTo-Json -Depth 4|Set-Content "$Output.ime.json" -Encoding utf8
  return [ordered]@{value=$value.Current.Value;language=$profiles.Current().Language;events=@($events).Count}
 } catch {
  $nodes=@(foreach($app in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)) {
   $owner=Get-Process -Id $app.Current.ProcessId -ErrorAction SilentlyContinue
   if($owner.ProcessName -in @('TextInputHost','ChsIME','ctfmon') -or $app.Current.ProcessId -eq $Window.Current.ProcessId) {
    $app.FindAll([System.Windows.Automation.TreeScope]::Subtree,[System.Windows.Automation.Condition]::TrueCondition) | Select-Object -First 250 | ForEach-Object { [ordered]@{name=$_.Current.Name;id=$_.Current.AutomationId;type=$_.Current.ControlType.ProgrammaticName} }
   }
  })
  $nodes|ConvertTo-Json -Depth 3|Set-Content "$Output.ime-failure.json" -Encoding utf8
  throw
 } finally {
  try { $profiles.Activate($previous) } finally { $profiles.Dispose() }
 }
}
