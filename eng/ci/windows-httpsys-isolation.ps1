# The http.sys URL isolation checks of the CS-WebUI blob endpoint (CsWebUiBlobEndpointChecks.HttpSys.cs), on an
# ephemeral GitHub-hosted Windows runner whose steps run elevated. Creates two standard users, reserves two ports for
# every user, starts another process as each user (windows-httpsys-agent.ps1) and runs the checks as the first user; then
# removes the processes, reservations, users, their profiles and their access to the workspace, also when the checks
# fail. The test assembly must be built (Release). A standard user cannot start a process as another user here
# (CreateProcessWithLogonW refuses a nested logon), so this script starts both other processes.
param([Parameter(Mandatory)][string]$TestAssembly)
$ErrorActionPreference = 'Stop'
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'These checks create local accounts and need an ephemeral GitHub-hosted runner.' }

$users = 'runic-httpsys-a', 'runic-httpsys-b'
$assembly = (Resolve-Path $TestAssembly).Path
$bin = Split-Path $assembly
$temp = 'C:\runic-httpsys-temp'
$agents = 'C:\runic-httpsys-agents'
$reservations = @()

function Free-Port {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop(); $port
}

# Each cleanup item runs on its own, so that one that fails does not skip the others; a failure is reported as a warning.
function Invoke-Cleanup([string]$What, [scriptblock]$Action) {
    try { & $Action } catch { Write-Warning "Cleanup could not ${What}: $_" }
}

function Remove-All {
    foreach ($url in $script:reservations) {
        Invoke-Cleanup "delete the reservation $url" { netsh http delete urlacl url=$url | Out-Null }
    }
    foreach ($name in $users) {
        $account = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
        if (-not $account) { continue }
        Invoke-Cleanup "stop the processes of $name" {
            Get-Process -IncludeUserName | Where-Object { $_.UserName -like "*\$name" } | Stop-Process -Force -ErrorAction Continue
            Start-Sleep -Seconds 1
        }
        Invoke-Cleanup "remove the profile of $name" {
            Get-CimInstance Win32_UserProfile -Filter "SID = '$($account.SID.Value)'" | Remove-CimInstance -ErrorAction Continue
        }
        Invoke-Cleanup "remove the access of $name to $bin" { icacls $bin /remove $name /T /C /Q | Out-Null }
        Invoke-Cleanup "remove the access of $name to the workspace" { icacls $env:GITHUB_WORKSPACE /remove $name /C /Q | Out-Null }
        Invoke-Cleanup "remove the user $name" { Remove-LocalUser -Name $name -ErrorAction Continue }
    }
    Invoke-Cleanup "remove $temp and $agents" { Remove-Item -Recurse -Force $temp, $agents -ErrorAction Continue }
}

try {
    $passwords = @{}
    foreach ($name in $users) {
        $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'Aa1!'
        Write-Output "::add-mask::$password"
        New-LocalUser -Name $name -Password (ConvertTo-SecureString $password -AsPlainText -Force) -PasswordNeverExpires -AccountNeverExpires | Out-Null
        Add-LocalGroupMember -Group Users -Member $name -ErrorAction SilentlyContinue
        $passwords[$name] = $password
    }
    # The first user reads the workspace and writes the test results beside the assembly and its temporary files.
    icacls $env:GITHUB_WORKSPACE /grant "$($users[0]):(OI)(CI)RX" /C /Q | Out-Null
    icacls $bin /grant "$($users[0]):(OI)(CI)M" /T /C /Q | Out-Null
    New-Item -ItemType Directory -Force $temp | Out-Null
    icacls $temp /grant "$($users[0]):(OI)(CI)M" /C /Q | Out-Null

    # The other processes: one of the first user, one of the second; each reads the requests of the checks from a
    # folder that both the check and it may write.
    New-Item -ItemType Directory -Force "$agents\same", "$agents\other" | Out-Null
    Copy-Item "$PSScriptRoot\windows-httpsys-agent.ps1" "$agents\agent.ps1"
    icacls $agents /grant 'BUILTIN\Users:(OI)(CI)RX' /C /Q | Out-Null
    icacls "$agents\same" /grant "$($users[0]):(OI)(CI)M" /C /Q | Out-Null
    icacls "$agents\other" /grant "$($users[0]):(OI)(CI)M" "$($users[1]):(OI)(CI)M" /C /Q | Out-Null
    foreach ($agent in @{ Folder = 'same'; User = $users[0] }, @{ Folder = 'other'; User = $users[1] }) {
        $credential = [pscredential]::new($agent.User, (ConvertTo-SecureString $passwords[$agent.User] -AsPlainText -Force))
        Start-Process (Get-Command pwsh).Source -Credential $credential -LoadUserProfile -WindowStyle Hidden -WorkingDirectory $agents `
            -ArgumentList '-NoProfile', '-NonInteractive', '-File', "$agents\agent.ps1", '-Directory', "$agents\$($agent.Folder)" | Out-Null
    }

    # A reservation of a whole port and one of a path under a port, for every user.
    $whole = Free-Port
    $under = Free-Port
    foreach ($url in "http://localhost:$whole/", "http://localhost:$under/_runic/") {
        netsh http add urlacl url=$url user=BUILTIN\Users listen=yes | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "netsh could not reserve $url." }
        $script:reservations += $url
    }

    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
    foreach ($argument in $assembly, '--treenode-filter',
        '/*/*/CsWebUiBlobEndpointChecks/(AnotherProcessCannotRegisterAUrlUnderTheEndpoint)|(APortThatAUrlReservationNamesIsNotUsed)|(UrlReservationPortsAreRead)',
        '--timeout', '5m', '--output', 'detailed') { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $bin
    $start.UserName = $users[0]
    $start.Domain = '.'
    $start.LoadUserProfile = $true
    $start.Password = ConvertTo-SecureString $passwords[$users[0]] -AsPlainText -Force
    foreach ($name in 'TEMP', 'TMP') { $start.Environment[$name] = $temp }
    $start.Environment['DOTNET_CLI_HOME'] = $temp
    foreach ($name in 'GITHUB_STEP_SUMMARY', 'GITHUB_OUTPUT', 'GITHUB_ENV', 'GITHUB_PATH', 'GITHUB_STATE') { $start.Environment.Remove($name) | Out-Null }
    $start.Environment['RUNIC_TEST_HTTPSYS'] = '1'
    $start.Environment['RUNIC_TEST_HTTPSYS_AGENTS'] = $agents
    $start.Environment['RUNIC_TEST_HTTPSYS_RESERVED_PORTS'] = "$whole,$under"
    Write-Output "Running the http.sys checks as $($users[0]) (reserved: $($script:reservations -join ', '))"
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(8 * 60 * 1000)) { $process.Kill($true); throw 'The http.sys checks did not finish in 8 minutes.' }
    $process.WaitForExit()
    Write-Output $output.Result
    if ($errors.Result) { Write-Output $errors.Result }
    Get-ChildItem $agents -Recurse -Filter '*.log' | ForEach-Object { Write-Output "--- $($_.Directory.Name)/$($_.Name)"; Get-Content $_.FullName }
    if ($process.ExitCode -ne 0) { throw "The http.sys checks failed ($($process.ExitCode))." }
}
finally {
    Remove-All
}
