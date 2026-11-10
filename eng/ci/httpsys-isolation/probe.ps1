# Probe of http.sys URL reservation isolation between processes and standard users, run elevated on an ephemeral
# GitHub-hosted Windows runner. Creates two standard users, starts listeners as them, sends requests and prints what
# each listener saw. Exploratory: prints facts, fails only when the probe itself cannot run.
$ErrorActionPreference = 'Stop'
$root = 'C:\httpsys-probe'
New-Item -ItemType Directory -Force $root | Out-Null
Copy-Item "$PSScriptRoot\serve.ps1" $root
icacls $root /grant 'Users:(OI)(CI)M' /C /Q | Out-Null
$pwsh = (Get-Command pwsh).Source
$credentials = @{}
foreach ($name in 'runic-probe-a', 'runic-probe-b') {
    $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'Aa1!'
    Write-Output "::add-mask::$password"
    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    if (-not (Get-LocalUser -Name $name -ErrorAction SilentlyContinue)) {
        New-LocalUser -Name $name -Password $secure -PasswordNeverExpires -AccountNeverExpires | Out-Null
    } else { Set-LocalUser -Name $name -Password $secure }
    Add-LocalGroupMember -Group Users -Member $name -ErrorAction SilentlyContinue
    $credentials[$name] = [pscredential]::new($name, $secure)
}
[Console]::Out.Flush()
$script:processes = @()
function FreePort {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start()
    $port = $probe.LocalEndpoint.Port; $probe.Stop(); $port
}
function Start-As([string]$user, [string]$name, [string[]]$arguments) {
    $out = Join-Path $root "$name.log"
    Remove-Item $out -ErrorAction SilentlyContinue
    $all = @('-NoProfile', '-NonInteractive', '-File', "$root\serve.ps1", '-Out', $out, '-Tag', $name) + $arguments
    $quoted = $all | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }
    $process = if ($user) {
        Start-Process $pwsh -ArgumentList $quoted -Credential $credentials[$user] -LoadUserProfile -WorkingDirectory $root -PassThru -WindowStyle Hidden
    } else {
        Start-Process $pwsh -ArgumentList $quoted -WorkingDirectory $root -PassThru -WindowStyle Hidden
    }
    $script:processes += $process
    $until = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $until) {
        if ((Test-Path $out) -and ((Get-Content $out) -match '^(started|error)')) { break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 200
    }
    $state = if (Test-Path $out) { (Get-Content $out) -match '^(started|error)' | Select-Object -First 1 } else { "no output (exited=$($process.HasExited))" }
    Write-Output "  $name as $($user ?? 'runner'): $state"
}
function Request([int]$port, [string]$path, [string]$family) {
    $body = & curl.exe -s $family -m 5 -H 'X-Probe-Token: secret-token' -o - -w ' [%{http_code}]' "http://localhost:$port$path" 2>&1
    Write-Output "  GET $family http://localhost:$port$path -> $body"
}
function Show([string[]]$names) {
    foreach ($name in $names) {
        $out = Join-Path $root "$name.log"
        if (Test-Path $out) { Get-Content $out | ForEach-Object { Write-Output "    $name| $_" } }
    }
}
function Stop-All {
    foreach ($process in $script:processes) { try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch {} }
    $script:processes = @()
    Start-Sleep -Milliseconds 500
}
function Case([string]$title, [scriptblock]$body) {
    Write-Output ""
    Write-Output "== $title"
    try { & $body } catch { Write-Output "  probe error: $_" }
    finally { Stop-All }
}

Write-Output "Windows: $([Environment]::OSVersion.VersionString); $((Get-CimInstance Win32_OperatingSystem).Caption)"

Case '1. Same user: A holds the port root, B registers a longer prefix' {
    $port = FreePort
    Start-As 'runic-probe-a' 'A1' @('-Port', $port, '-Prefixes', "http://localhost:$port/")
    Start-As 'runic-probe-a' 'B1' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/")
    Request $port '/_runic/blobs/abc' '-4'; Request $port '/_runic/blobs/abc' '-6'; Request $port '/other' '-4'
    Show 'A1', 'B1'
}
Case '2. Another standard user: A holds the port root, B registers a longer prefix' {
    $port = FreePort
    Start-As 'runic-probe-a' 'A2' @('-Port', $port, '-Prefixes', "http://localhost:$port/")
    Start-As 'runic-probe-b' 'B2' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/")
    Request $port '/_runic/blobs/abc' '-4'; Request $port '/_runic/blobs/abc' '-6'; Request $port '/other' '-4'
    Show 'A2', 'B2'
}
Case '3. Another standard user: B registers the same root, a strong and a weak wildcard, an IP-bound prefix' {
    $port = FreePort
    Start-As 'runic-probe-a' 'A3' @('-Port', $port, '-Prefixes', "http://localhost:$port/")
    Start-As 'runic-probe-b' 'B3root' @('-Port', $port, '-Prefixes', "http://localhost:$port/")
    Start-As 'runic-probe-b' 'B3plus' @('-Port', $port, '-Prefixes', "http://+:$port/_runic/")
    Start-As 'runic-probe-b' 'B3star' @('-Port', $port, '-Prefixes', "http://*:$port/_runic/")
    Start-As 'runic-probe-b' 'B3ip' @('-Port', $port, '-Prefixes', "http://127.0.0.1:$port/_runic/")
    Start-As 'runic-probe-b' 'B3upper' @('-Port', $port, '-Prefixes', "http://LOCALHOST:$port/_RUNIC/")
    Request $port '/_runic/blobs/abc' '-4'
    Show 'A3', 'B3root', 'B3plus', 'B3star', 'B3ip', 'B3upper'
    Write-Output "  netsh http show servicestate as another standard user:"
    $out = Join-Path $root 'servicestate.txt'
    $cmd = Start-Process cmd.exe -ArgumentList '/c', "netsh http show servicestate view=requestq > `"$out`" 2>&1" -Credential $credentials['runic-probe-b'] -LoadUserProfile -WorkingDirectory $root -PassThru -WindowStyle Hidden -Wait
    $text = Get-Content $out -Raw
    Write-Output "    exit $($cmd.ExitCode); lists A's port: $($text -match "localhost:$port/"); lines: $(($text -split "`n").Count)"
    ($text -split "`n") | Select-Object -First 12 | ForEach-Object { Write-Output "    | $_" }
    ($text -split "`n") | Select-String "localhost:$port" | ForEach-Object { Write-Output "    match| $_" }
}
Case '4. Another standard user: B registers a longer prefix before A starts' {
    $port = FreePort
    Start-As 'runic-probe-b' 'B4' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/")
    Start-As 'runic-probe-a' 'A4' @('-Port', $port, '-Prefixes', "http://localhost:$port/")
    Request $port '/_runic/blobs/abc' '-4'
    Show 'A4', 'B4'
}
Case '5. Another standard user: A holds exclusive loopback sockets, B registers a longer prefix through http.sys' {
    $port = FreePort
    Start-As 'runic-probe-a' 'A5' @('-Port', $port, '-Exclusive')
    Start-As 'runic-probe-b' 'B5' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/")
    Request $port '/_runic/blobs/abc' '-4'; Request $port '/_runic/blobs/abc' '-6'
    Show 'A5', 'B5'
    netstat -ano | Select-String ":$port\s" | ForEach-Object { Write-Output "    netstat| $_" }
}
Case '6. Another standard user: B registers through http.sys first, then A tries exclusive loopback sockets' {
    $port = FreePort
    Start-As 'runic-probe-b' 'B6' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/")
    Start-As 'runic-probe-a' 'A6' @('-Port', $port, '-Exclusive')
    Request $port '/_runic/blobs/abc' '-4'; Request $port '/_runic/blobs/abc' '-6'
    Show 'A6', 'B6'
}
Case '7. Another standard user: A holds exclusive loopback sockets, B binds sockets on the port' {
    $port = FreePort
    Start-As 'runic-probe-a' 'A7' @('-Port', $port, '-Exclusive')
    Start-As 'runic-probe-b' 'B7' @('-Port', $port, '-Exclusive')
    Show 'A7', 'B7'
}

Write-Output ""
Write-Output "== Reservations on this runner (netsh http show urlacl, as admin)"
netsh http show urlacl | Select-String 'Reserved URL' | ForEach-Object { Write-Output "  $($_.Line.Trim())" }

function Reserve([string]$url, [scriptblock]$body) {
    netsh http add urlacl url=$url user=BUILTIN\Users listen=yes | Out-Null
    try { & $body } finally { netsh http delete urlacl url=$url | Out-Null }
}
Case '8. An administrator reserved http://+:P/ for Users: A holds localhost root, B registers a strong-wildcard longer prefix after' {
    $port = FreePort
    Reserve "http://+:$port/" {
        Start-As 'runic-probe-a' 'A8' @('-Port', $port, '-Prefixes', "http://localhost:$port/")
        Start-As 'runic-probe-b' 'B8' @('-Port', $port, '-Prefixes', "http://+:$port/_runic/")
        Start-As 'runic-probe-b' 'B8root' @('-Port', $port, '-Prefixes', "http://+:$port/")
        Request $port '/_runic/blobs/abc' '-4'; Request $port '/other' '-4'
        Show 'A8', 'B8', 'B8root'
    }
}
Case '9. An administrator reserved http://localhost:P/ for Users: A holds the root, B registers a longer prefix after' {
    $port = FreePort
    Reserve "http://localhost:$port/" {
        Start-As 'runic-probe-a' 'A9' @('-Port', $port, '-Prefixes', "http://localhost:$port/")
        Start-As 'runic-probe-b' 'B9' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/")
        Request $port '/_runic/blobs/abc' '-4'
        Show 'A9', 'B9'
    }
}
Case '10. An administrator reserved http://+:P/ for Users: A holds exclusive loopback sockets, B registers through http.sys' {
    $port = FreePort
    Reserve "http://+:$port/" {
        Start-As 'runic-probe-a' 'A10' @('-Port', $port, '-Exclusive')
        Start-As 'runic-probe-b' 'B10' @('-Port', $port, '-Prefixes', "http://+:$port/_runic/")
        Start-As 'runic-probe-b' 'B10star' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/")
        Request $port '/_runic/blobs/abc' '-4'; Request $port '/_runic/blobs/abc' '-6'
        Show 'A10', 'B10', 'B10star'
    }
}
Case '11. Same user, two prefixes deeper than the root: B registers /_runic/blobs/abc/ after A holds the root' {
    $port = FreePort
    Start-As 'runic-probe-a' 'A11' @('-Port', $port, '-Prefixes', "http://localhost:$port/")
    Start-As 'runic-probe-a' 'B11' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/blobs/abc/")
    Start-As 'runic-probe-b' 'B11b' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/upload/")
    Request $port '/_runic/blobs/abc' '-4'; Request $port '/_runic/upload' '-4'
    Show 'A11', 'B11', 'B11b'
}
Case '12. A standard user lists reservations; a reservation nested under the port' {
    $port = FreePort
    Reserve "http://localhost:$port/_runic/" {
        $out = Join-Path $root 'urlacl.txt'
        $cmd = Start-Process cmd.exe -ArgumentList '/c', "netsh http show urlacl > `"$out`" 2>&1" -Credential $credentials['runic-probe-b'] -LoadUserProfile -WorkingDirectory $root -PassThru -WindowStyle Hidden -Wait
        $text = Get-Content $out -Raw
        Write-Output "  netsh http show urlacl as a standard user: exit $($cmd.ExitCode); lists the reservation: $($text -match "localhost:$port/_runic/")"
        Start-As 'runic-probe-a' 'A12' @('-Port', $port, '-Prefixes', "http://localhost:$port/")
        Start-As 'runic-probe-b' 'B12' @('-Port', $port, '-Prefixes', "http://localhost:$port/_runic/")
        Request $port '/_runic/blobs/abc' '-4'
        Show 'A12', 'B12'
    }
}
