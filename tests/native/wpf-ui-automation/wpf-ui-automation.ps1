# UI Automation check of a WPF window with an embedded Runic web View: what Narrator reads and how keyboard focus
# moves between the WPF shell and the WebView2 content. It drives the published examples/wpf-hybrid-editor app.
# See README.md beside this script.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$Executable,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [switch]$Narrator
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
Add-Type -AssemblyName System.Windows.Forms
Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase @'
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;

public static class RunicWpfInput {
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public IntPtr Extra; }
    // MOUSEINPUT is the union's largest member and sets INPUT's size.
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X; public int Y; public uint Data; public uint Flags; public uint Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);

    static Input Key(ushort key, bool up) {
        var input = new Input { Type = 1 };
        input.Data.Keyboard = new KeyboardInput { Key = key, Scan = (ushort)MapVirtualKey(key, 0), Flags = up ? 2u : 0u };
        return input;
    }

    // Presses the keys in order and releases them in reverse, as one SendInput batch.
    public static void Chord(params ushort[] keys) {
        var inputs = new Input[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Key(keys[i], false);
        for (int i = 0; i < keys.Length; i++) inputs[keys.Length + i] = Key(keys[keys.Length - 1 - i], true);
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}

// Records UI Automation focus-changed events, the events Narrator follows, from the client's own thread.
public static class RunicFocusEvents {
    static readonly ConcurrentQueue<string> events = new ConcurrentQueue<string>();
    static AutomationFocusChangedEventHandler handler;
    public static void Start() {
        handler = (sender, args) => {
            try {
                var element = (AutomationElement)sender;
                events.Enqueue(element.Current.FrameworkId + "|" + element.Current.ControlType.ProgrammaticName + "|" + element.Current.Name);
            } catch (Exception error) { events.Enqueue("error|" + error.GetType().Name + "|"); }
        };
        Automation.AddAutomationFocusChangedEventHandler(handler);
    }
    public static void Stop() { if (handler != null) Automation.RemoveAutomationFocusChangedEventHandler(handler); handler = null; }
    public static string[] Drain() { var list = new System.Collections.Generic.List<string>(); string item; while (events.TryDequeue(out item)) list.Add(item); return list.ToArray(); }
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$Scope = [System.Windows.Automation.TreeScope]
$VkTab = [uint16]0x09; $VkShift = [uint16]0x10; $VkReturn = [uint16]0x0D; $VkControl = [uint16]0x11; $VkInsert = [uint16]0x2D
$checks = [System.Collections.Generic.List[object]]::new()

function Condition([System.Windows.Automation.AutomationProperty]$Property, $Value) {
    New-Object System.Windows.Automation.PropertyCondition($Property, $Value)
}
function And([System.Windows.Automation.Condition[]]$Conditions) { New-Object System.Windows.Automation.AndCondition($Conditions) }

function Find-Element($Root, $Condition, [int]$TimeoutSeconds = 30, [string]$What = "$Condition") {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $found = $Root.FindFirst($Scope::Descendants, $Condition)
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "UI Automation element was not available: $What"
}

function Describe($Element) {
    if ($null -eq $Element) { return '<none>' }
    $c = $Element.Current
    "$($c.FrameworkId)/$($c.ControlType.ProgrammaticName -replace '^ControlType\.', '') '$($c.Name)' class=$($c.ClassName)"
}

function Pass([string]$Name, [string]$Detail) {
    $checks.Add([ordered]@{ check = $Name; result = 'pass'; detail = $Detail })
    Write-Output "PASS $Name - $Detail"
}

function Assert-Check([bool]$Condition, [string]$Name, [string]$Detail) {
    if (-not $Condition) {
        $checks.Add([ordered]@{ check = $Name; result = 'fail'; detail = $Detail })
        throw "FAIL $Name - $Detail"
    }
    Pass $Name $Detail
}

# Waits until UI Automation's focused element matches, then requires a focus-changed event for it.
function Wait-Focus([string]$Step, [string]$Name, $ControlType, [string]$FrameworkId, [int]$TimeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $focused = $null
    do {
        try { $focused = $AE::FocusedElement } catch { $focused = $null }
        if ($null -ne $focused -and $focused.Current.Name -eq $Name -and $focused.Current.ControlType -eq $ControlType -and $focused.Current.FrameworkId -eq $FrameworkId) { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    $actual = Describe $focused
    Assert-Check ($null -ne $focused -and $focused.Current.Name -eq $Name -and $focused.Current.ControlType -eq $ControlType -and $focused.Current.FrameworkId -eq $FrameworkId) `
        "$Step focus" "FocusedElement is $actual"
    Assert-Check ($focused.Current.HasKeyboardFocus) "$Step keyboard focus" "HasKeyboardFocus on '$Name'"
    $expected = "$FrameworkId|$($ControlType.ProgrammaticName)|$Name"
    $eventDeadline = [DateTime]::UtcNow.AddSeconds(5)
    $seen = @()
    do {
        $seen += [RunicFocusEvents]::Drain()
        if ($seen -contains $expected) { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $eventDeadline)
    Assert-Check ($seen -contains $expected) "$Step focus event" "focus-changed events: $($seen -join '; ')"
    return $focused
}

function Send-Keys([string]$Step, [uint16[]]$Keys) {
    if ([RunicWpfInput]::GetForegroundWindow() -ne $mainHandle) {
        [RunicWpfInput]::SetForegroundWindow($mainHandle) | Out-Null
        Start-Sleep -Milliseconds 200
    }
    Assert-Check ([RunicWpfInput]::GetForegroundWindow() -eq $mainHandle) "$Step foreground" 'the app window receives the keys'
    [RunicFocusEvents]::Drain() | Out-Null
    [RunicWpfInput]::Chord($Keys)
}

# The UI Automation ancestors of an element up to the main window, nearest first.
function Get-Ancestry($Element) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $chain = @()
    $node = $walker.GetParent($Element)
    while ($null -ne $node -and -not [System.Windows.Automation.Automation]::Compare($node, $mainWindow)) {
        $chain += $node
        $node = $walker.GetParent($node)
    }
    if ($null -eq $node) { throw "The element is not under the main window: $(Describe $Element)" }
    return $chain
}

function Save-Tree($Root, [string]$Path) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $lines = [System.Collections.Generic.List[string]]::new()
    function Visit($Node, [int]$Depth) {
        if ($Depth -gt 40) { return }
        $lines.Add(('  ' * $Depth) + (Describe $Node) + " id=$($Node.Current.AutomationId) type='$($Node.Current.LocalizedControlType)'")
        $child = $walker.GetFirstChild($Node)
        while ($null -ne $child) { Visit $child ($Depth + 1); $child = $walker.GetNextSibling($child) }
    }
    Visit $Root 0
    $lines | Set-Content -LiteralPath $Path -Encoding utf8
}

# Narrator reads the focused item again (Narrator+Tab), then Narrator+Ctrl+X copies its last spoken phrase (Narrator
# key: Insert). Ctrl first stops speech still queued from the focus change or an earlier copy confirmation.
function Get-NarratorSpeech([string]$Step) {
    Start-Sleep -Seconds 2
    [RunicWpfInput]::Chord($VkControl)
    [RunicWpfInput]::Chord($VkInsert, $VkTab)
    Start-Sleep -Seconds 5
    [System.Windows.Forms.Clipboard]::Clear()
    [RunicWpfInput]::Chord($VkInsert, $VkControl, [uint16]0x58)
    $deadline = [DateTime]::UtcNow.AddSeconds(5); $spoken = ''
    do { $spoken = [System.Windows.Forms.Clipboard]::GetText(); if ($spoken) { break }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    $spoken | Set-Content -LiteralPath "$ReceiptPath.narrator-$Step.txt" -Encoding utf8
    return $spoken
}

$process = $null
$narratorProcess = $null
$clipboard = $null
$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
if ($sessionId -eq 0) { throw 'Windows UI Automation requires an interactive session, not Session 0.' }
if ($Narrator -and (Get-Process Narrator -ErrorAction SilentlyContinue)) { throw 'Stop the running Narrator before this check; it leaves existing sessions alone.' }
$started = [DateTime]::UtcNow
try {
    $process = Start-Process -FilePath $Executable -PassThru -WorkingDirectory (Split-Path -Parent $Executable) `
        -RedirectStandardOutput "$ReceiptPath.stdout" -RedirectStandardError "$ReceiptPath.stderr"
    [ordered]@{ processId = $process.Id; sessionId = $sessionId; executable = $Executable } | ConvertTo-Json | Set-Content -LiteralPath $runningPath -Encoding utf8
    [RunicFocusEvents]::Start()

    # The WPF shell: the note list, then the editor page with its native editor.
    $mainWindow = Find-Element $AE::RootElement (And @((Condition $AE::ProcessIdProperty $process.Id), (Condition $AE::ControlTypeProperty $CT::Window))) 60 'the app window'
    $mainHandle = [IntPtr]$mainWindow.Current.NativeWindowHandle
    Assert-Check ($mainWindow.Current.Name -eq 'Hybrid notes' -and $mainWindow.Current.FrameworkId -eq 'WPF') 'shell window' (Describe $mainWindow)
    $list = Find-Element $mainWindow (Condition $AE::ControlTypeProperty $CT::List) 30 'the note list'
    $first = Find-Element $list (Condition $AE::ControlTypeProperty $CT::ListItem) 30 'a note'
    $first.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $open = Find-Element $mainWindow (And @((Condition $AE::NameProperty 'Open note'), (Condition $AE::ControlTypeProperty $CT::Button)))
    $open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $switch = Find-Element $mainWindow (And @((Condition $AE::NameProperty 'Use web editor'), (Condition $AE::ControlTypeProperty $CT::Button))) 30 'the Use web editor button'
    $switch.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    # The embedded WebView2 content: its document and the page's semantic tree, inside the WPF window.
    $document = Find-Element $mainWindow (And @((Condition $AE::ControlTypeProperty $CT::Document), (Condition $AE::NameProperty 'Note editor'))) 60 'the web editor document'
    Save-Tree $mainWindow "$ReceiptPath.tree.txt"
    Assert-Check ($document.Current.FrameworkId -eq 'Chrome') 'web document' (Describe $document)
    $ancestry = @(Get-Ancestry $document)
    $hostPane = $ancestry | Where-Object { $_.Current.FrameworkId -eq 'WPF' } | Select-Object -First 1
    Assert-Check ($null -ne $hostPane -and $hostPane.Current.ControlType -eq $CT::Pane -and $hostPane.Current.Name -eq 'Web note editor') `
        'host pane' "nearest WPF ancestor of the document: $(Describe $hostPane); chain: $(($ancestry | ForEach-Object { Describe $_ }) -join ' < ')"
    $heading = Find-Element $document (And @((Condition $AE::NameProperty 'Web editor'), (Condition $AE::ControlTypeProperty $CT::Text))) 30 'the heading'
    Assert-Check ($heading.Current.LocalizedControlType -match '(?i)heading|.berschrift') 'web heading' "$(Describe $heading) as '$($heading.Current.LocalizedControlType)'"
    $title = Find-Element $document (And @((Condition $AE::NameProperty 'Title'), (Condition $AE::ControlTypeProperty $CT::Edit))) 30 'the Title input'
    $body = Find-Element $document (And @((Condition $AE::NameProperty 'Body'), (Condition $AE::ControlTypeProperty $CT::Edit))) 30 'the Body input'
    Pass 'web inputs' "$(Describe $title); $(Describe $body)"
    $save = Find-Element $document (And @((Condition $AE::NameProperty 'Save'), (Condition $AE::ControlTypeProperty $CT::Button))) 30 'the Save button'
    $cancel = Find-Element $document (And @((Condition $AE::NameProperty 'Cancel save'), (Condition $AE::ControlTypeProperty $CT::Button))) 30 'the Cancel save button'
    Assert-Check (-not $save.Current.IsEnabled -and -not $cancel.Current.IsEnabled) 'web buttons' "$(Describe $save) enabled=$($save.Current.IsEnabled); $(Describe $cancel) enabled=$($cancel.Current.IsEnabled)"
    # The Title input shows the model's title once the Bridge connected.
    $titleValue = $title.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not $titleValue.Current.Value -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Assert-Check ([bool]$titleValue.Current.Value) 'web content connected' "Title shows the model's '$($titleValue.Current.Value)'"

    # Keyboard: WPF -> web content -> WPF, both directions. The WPF window's traversal cycles, so after the
    # document's last stop comes the window's first control, Back to notes.
    $reload = Find-Element $mainWindow (And @((Condition $AE::NameProperty 'Reload web editor'), (Condition $AE::ControlTypeProperty $CT::Button)))
    [RunicWpfInput]::SetForegroundWindow($mainHandle) | Out-Null
    $reload.SetFocus()
    Wait-Focus 'start on Reload' 'Reload web editor' $CT::Button 'WPF' | Out-Null
    function Test-TabRound([string]$Round) {
        Send-Keys "$Round Tab into web" @($VkTab)
        Wait-Focus "$Round Tab into web" 'Title' $CT::Edit 'Chrome' | Out-Null
        Send-Keys "$Round Tab in web" @($VkTab)
        Wait-Focus "$Round Tab in web" 'Body' $CT::Edit 'Chrome' | Out-Null
        Send-Keys "$Round Tab out of web" @($VkTab)
        Wait-Focus "$Round Tab out of web" 'Back to notes' $CT::Button 'WPF' | Out-Null
        Send-Keys "$Round Shift+Tab into web" @($VkShift, $VkTab)
        Wait-Focus "$Round Shift+Tab into web" 'Body' $CT::Edit 'Chrome' | Out-Null
        Send-Keys "$Round Shift+Tab in web" @($VkShift, $VkTab)
        Wait-Focus "$Round Shift+Tab in web" 'Title' $CT::Edit 'Chrome' | Out-Null
        Send-Keys "$Round Shift+Tab out of web" @($VkShift, $VkTab)
        Wait-Focus "$Round Shift+Tab out of web" 'Reload web editor' $CT::Button 'WPF' | Out-Null
    }
    Test-TabRound 'keyboard'

    # Reload from the keyboard: Enter on Reload replaces the WebView; focus stays on the WPF button and Tab enters
    # the new document.
    Send-Keys 'reload' @($VkReturn)
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 200
        $reloaded = $mainWindow.FindFirst($Scope::Descendants, (And @((Condition $AE::ControlTypeProperty $CT::Document), (Condition $AE::NameProperty 'Note editor'))))
    } while (([DateTime]::UtcNow -lt $deadline) -and ($null -eq $reloaded -or [System.Windows.Automation.Automation]::Compare($reloaded, $document)))
    Assert-Check ($null -ne $reloaded -and -not [System.Windows.Automation.Automation]::Compare($reloaded, $document)) 'reload replaced the document' (Describe $reloaded)
    $title = Find-Element $reloaded (And @((Condition $AE::NameProperty 'Title'), (Condition $AE::ControlTypeProperty $CT::Edit))) 30 'the reloaded Title input'
    $focused = $AE::FocusedElement
    Assert-Check ($focused.Current.Name -eq 'Reload web editor' -and $focused.Current.FrameworkId -eq 'WPF') 'focus after reload' "FocusedElement is $(Describe $focused)"
    Test-TabRound 'after reload'

    $narratorResults = @()
    if ($Narrator) {
        # Narrator runs while the same keyboard round repeats: focus events still reach UI Automation clients and
        # Narrator speaks the WPF button, the web document's inputs and the host it entered.
        $clipboard = [System.Windows.Forms.Clipboard]::GetDataObject()
        $narratorProcess = Start-Process "$env:windir\System32\Narrator.exe" -PassThru
        Start-Sleep -Seconds 5
        $narratorProcess = Get-Process Narrator | Where-Object { $_.SessionId -eq $sessionId -and $_.StartTime.ToUniversalTime() -ge $started } | Select-Object -First 1
        if (-not $narratorProcess) { throw 'Narrator did not start in the test session.' }
        @{ processId = $narratorProcess.Id; startTicks = $narratorProcess.StartTime.ToUniversalTime().Ticks; sessionId = $sessionId } |
            ConvertTo-Json | Set-Content -LiteralPath "$ReceiptPath.narrator-running" -Encoding utf8
        # Narrator Home may open in front on start; minimize it so the keys reach the app.
        $narratorHome = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $narratorHome = $AE::RootElement.FindFirst($Scope::Children, (Condition $AE::ProcessIdProperty $narratorProcess.Id))
            if ($narratorHome) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($narratorHome) {
            try { $narratorHome.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState('Minimized') }
            catch { Write-Warning "Could not minimize Narrator Home: $_" }
        }
        [RunicWpfInput]::SetForegroundWindow($mainHandle) | Out-Null
        Start-Sleep -Seconds 2
        $reload.SetFocus()
        Wait-Focus 'Narrator start on Reload' 'Reload web editor' $CT::Button 'WPF' | Out-Null
        Test-TabRound 'with Narrator'
        Assert-Check (-not $process.HasExited) 'app with Narrator' 'the app kept running while Narrator read it'
        # Speech: Narrator's last phrase for the focused element, with English or German role words. The round
        # above ended on Reload.
        Send-Keys 'Narrator into web' @($VkTab)
        Wait-Focus 'Narrator into web' 'Title' $CT::Edit 'Chrome' | Out-Null
        $spoken = Get-NarratorSpeech 'title'
        Assert-Check ($spoken -like '*Title*' -and $spoken -match '(?i)\b(edit|bearbeiten|eingabe|textfeld|text)') 'Narrator reads the web input' "speech: $spoken"
        $narratorResults += [ordered]@{ step = 'title'; spoken = $spoken }
        Send-Keys 'Narrator Body' @($VkTab); Wait-Focus 'Narrator Body' 'Body' $CT::Edit 'Chrome' | Out-Null
        Send-Keys 'Narrator out of web' @($VkTab)
        Wait-Focus 'Narrator out of web' 'Back to notes' $CT::Button 'WPF' | Out-Null
        $spoken = Get-NarratorSpeech 'back'
        Assert-Check ($spoken -like '*Back to notes*' -and $spoken -match '(?i)\b(button|schaltfl.che)\b') 'Narrator reads the WPF button after leaving the web View' "speech: $spoken"
        $narratorResults += [ordered]@{ step = 'back'; spoken = $spoken }
    }

    $receipt = [ordered]@{
        sessionId = $sessionId
        windowName = $mainWindow.Current.Name
        snapshotName = $reloaded.Current.Name
        executable = $Executable
        narrator = [bool]$Narrator
        narratorSpeech = $narratorResults
        checks = $checks
        passed = $true
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReceiptPath -Encoding utf8
    Write-Output "PASS WPF UI Automation: $($checks.Count) checks"
}
catch {
    [ordered]@{ error = "$_"; checks = $checks; focused = (Describe ([System.Windows.Automation.AutomationElement]::FocusedElement)) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$ReceiptPath.failure.json" -Encoding utf8
    if ($mainWindow) { try { Save-Tree $mainWindow "$ReceiptPath.failure-tree.txt" } catch { } }
    throw
}
finally {
    [RunicFocusEvents]::Stop()
    try {
        if ($narratorProcess -and -not $narratorProcess.HasExited) {
            [RunicWpfInput]::Chord($VkInsert, [uint16]0x1B) # Narrator+Escape exits Narrator.
            if (-not $narratorProcess.WaitForExit(5000)) { Stop-Process -Id $narratorProcess.Id -Force }
        }
        Remove-Item -LiteralPath "$ReceiptPath.narrator-running" -ErrorAction SilentlyContinue
        if ($clipboard) { [System.Windows.Forms.Clipboard]::SetDataObject($clipboard, $true) }
    }
    finally {
        if ($process -and -not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
        }
        Remove-Item -LiteralPath $runningPath -ErrorAction SilentlyContinue
    }
}
