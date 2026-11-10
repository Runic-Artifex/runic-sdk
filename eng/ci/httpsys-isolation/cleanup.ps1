foreach ($name in 'runic-probe-a', 'runic-probe-b') {
    $account = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
    if (-not $account) { continue }
    Get-Process -IncludeUserName | Where-Object { $_.UserName -like "*\$name" } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Get-CimInstance Win32_UserProfile -Filter "SID = '$($account.SID.Value)'" | Remove-CimInstance -ErrorAction SilentlyContinue
    Remove-LocalUser -Name $name
}
Remove-Item -Recurse -Force C:\httpsys-probe -ErrorAction SilentlyContinue
