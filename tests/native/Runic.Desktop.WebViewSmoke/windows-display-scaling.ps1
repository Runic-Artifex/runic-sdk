# Test-only Windows Settings automation for an unlocked, dedicated desktop.
function Set-RunicDisplayScale([int]$Percent, [ref]$PreviousPercent) {
    # Windows may preload a suspended Settings process without a window.
    # Reject an existing visible Settings UI, not the background process.
    $settingsLabel = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsLabel')))
    if ($null -ne $settingsLabel) {
        throw 'Close Windows Settings before running display-scale tests.'
    }
    $settingsProcess = $null
    try {
        Start-Process 'ms-settings:display'
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'SystemSettings_Display_Scaling_ItemSizeOverride_ComboBox')
        do {
            $settingsProcess = Get-Process SystemSettings -ErrorAction SilentlyContinue | Select-Object -First 1
            $combo = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $combo) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $combo) { throw 'Windows Settings did not expose the standard display-scale selector.' }
        $selection = [System.Windows.Automation.SelectionPattern]$combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
        $selected = $selection.Current.GetSelection()
        if ($selected.Count -ne 1 -or $selected[0].Current.Name -notmatch '^(\d+)%') {
            throw 'Could not determine the original standard display scale.'
        }
        # Capture before mutation so the caller can restore even after a failure.
        $PreviousPercent.Value = [int]$Matches[1]
        if ($PreviousPercent.Value -eq $Percent) { return }
        ([System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $item = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
                $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
                $_.Current.Name -match "^$Percent%(?:\s|$)"
            } | Select-Object -First 1
            if ($null -ne $item) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $item) { throw "Windows does not offer $Percent% as a standard scale on this display." }
        ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        # Display changes propagate asynchronously to the compositor and apps.
        Start-Sleep -Seconds 2
        $selected = $selection.Current.GetSelection()
        if ($selected.Count -ne 1 -or $selected[0].Current.Name -notmatch "^$Percent%(?:\s|$)") {
            throw "Windows did not select $Percent% display scaling."
        }
    }
    finally {
        if ($settingsProcess -and -not $settingsProcess.HasExited) { $settingsProcess | Stop-Process }
    }
}
