[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Install', 'Remove')][string]$Action,
    [Parameter(Mandatory)][ValidatePattern('^Runic\.Tests\.Notifications\.[A-Za-z0-9.]+$')][string]$AppId,
    [string]$Executable,
    [string]$ProtocolReceipt
)
$ErrorActionPreference = 'Stop'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('StartMenu')) "Programs\$AppId.lnk"
$identityKey = "HKCU:\Software\Classes\AppUserModelId\$AppId"
$protocolKey = 'HKCU:\Software\Classes\runic-p1-test'
if ($Action -eq 'Remove') {
    if (Test-Path $protocolKey) {
        if ((Get-ItemProperty $protocolKey).RunicTestAppId -eq $AppId) { Remove-Item $protocolKey -Recurse }
    }
    foreach ($path in @($shortcutPath, $identityKey)) { if (Test-Path $path) { Remove-Item $path -Recurse } }
    return
}
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
if ($executablePath.Contains('"')) { throw 'Executable path cannot contain quotes.' }
if ((Test-Path $shortcutPath) -or (Test-Path $identityKey)) { throw 'Use a fresh test AppId; existing registration will not be overwritten.' }
# Check OS-generated settings too: deleting a shortcut does not make an identity fresh.
if (Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\$AppId") { throw 'This AppId already has notification history/settings; choose a new one.' }
if ($ProtocolReceipt) {
    $ProtocolReceipt = [IO.Path]::GetFullPath($ProtocolReceipt)
    if ($ProtocolReceipt.Contains('"') -or (Test-Path $ProtocolReceipt)) { throw 'Supply a fresh receipt path without quotes.' }
    if (Test-Path $protocolKey) { throw 'The test protocol is already registered; remove its previous test registration first.' }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RunicNotificationTestShortcut {
    [StructLayout(LayoutKind.Sequential)] public struct Key { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size=24)] public struct Value { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Text; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface Store {
        void Count(out uint count); void At(uint i, out Key key); void Get(ref Key key, out Value value);
        void Set(ref Key key, ref Value value); void Commit();
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode, PreserveSig=false)]
    static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr context, uint flags, ref Guid iid, out Store store);
    public static void Set(string path, string id) {
        Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"); Store store;
        SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out store);
        var key = new Key { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        var value = new Value { Type = 31, Text = Marshal.StringToCoTaskMemUni(id) };
        try { store.Set(ref key, ref value); store.Commit(); }
        finally { Marshal.FreeCoTaskMem(value.Text); Marshal.ReleaseComObject(store); }
    }
}
'@
try {
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executablePath
    $shortcut.WorkingDirectory = Split-Path $executablePath
    $shortcut.Save()
    [RunicNotificationTestShortcut]::Set($shortcutPath, $AppId)
    New-Item $identityKey -Force | Out-Null
    New-ItemProperty $identityKey -Name DisplayName -Value 'Runic notification test' -PropertyType ExpandString | Out-Null
    if ($ProtocolReceipt) {
        New-Item "$protocolKey\shell\open\command" -Force | Out-Null
        Set-Item $protocolKey -Value 'URL:Runic notification test'
        New-ItemProperty $protocolKey -Name 'URL Protocol' -Value '' | Out-Null
        New-ItemProperty $protocolKey -Name RunicTestAppId -Value $AppId | Out-Null
        Set-Item "$protocolKey\shell\open\command" -Value ('"{0}" --notification-activation "%1" "{1}"' -f $executablePath, $ProtocolReceipt)
    }
    Write-Output "Installed fresh notification test identity: $AppId"
} catch {
    foreach ($path in @($shortcutPath, $identityKey)) { if (Test-Path $path) { Remove-Item $path -Recurse } }
    if ($ProtocolReceipt -and (Test-Path $protocolKey)) { Remove-Item $protocolKey -Recurse }
    throw
}
