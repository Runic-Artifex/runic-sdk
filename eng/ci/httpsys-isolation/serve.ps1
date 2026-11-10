# Probe listener: an http.sys registration (HttpListener) or exclusive loopback sockets. Writes what happens to -Out.
param(
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$Tag,
    [int]$Port,
    [string[]]$Prefixes = @(),
    [switch]$Exclusive,
    [int]$Seconds = 45
)
$ErrorActionPreference = 'Stop'
function Log([string]$line) { Add-Content -LiteralPath $Out -Value $line -Encoding utf8 }
Log "user $([Security.Principal.WindowsIdentity]::GetCurrent().Name) elevated=$(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"
$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
if ($Exclusive) {
    $listeners = @()
    try {
        foreach ($address in [Net.IPAddress]::Loopback, [Net.IPAddress]::IPv6Loopback) {
            $listener = [Net.Sockets.TcpListener]::new($address, $Port)
            $listener.ExclusiveAddressUse = $true
            $listener.Start()
            $listeners += $listener
        }
    } catch {
        $inner = $_.Exception.InnerException ?? $_.Exception
        Log "error $($inner.ErrorCode) $($inner.Message)"
        exit 0
    }
    Log 'started'
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($listener in $listeners) {
            if (-not $listener.Pending()) { continue }
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $stream.ReadTimeout = 2000
                $buffer = [byte[]]::new(8192); $head = ''
                while (-not $head.Contains("`r`n`r`n")) {
                    $read = $stream.Read($buffer, 0, $buffer.Length)
                    if ($read -le 0) { break }
                    $head += [Text.Encoding]::ASCII.GetString($buffer, 0, $read)
                }
                $lines = $head -split "`r`n"
                $token = ($lines | Where-Object { $_ -like 'X-Probe-Token:*' }) -replace '^X-Probe-Token:\s*', ''
                Log "request $($lines[0]) token=$token peer=$($client.Client.RemoteEndPoint)"
                $body = [Text.Encoding]::ASCII.GetBytes($Tag)
                $reply = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n")
                $stream.Write($reply, 0, $reply.Length); $stream.Write($body, 0, $body.Length)
            } catch { Log "request-error $($_.Exception.Message)" }
            finally { $client.Dispose() }
        }
        Start-Sleep -Milliseconds 20
    }
    foreach ($listener in $listeners) { $listener.Stop() }
    exit 0
}
$listener = [Net.HttpListener]::new()
foreach ($prefix in $Prefixes) { $listener.Prefixes.Add($prefix) }
try { $listener.Start() }
catch {
    $error0 = $_.Exception
    while ($error0 -and $error0 -isnot [Net.HttpListenerException]) { $error0 = $error0.InnerException }
    $error0 = $error0 ?? $_.Exception
    Log "error $($error0.ErrorCode) $($error0.Message)"
    exit 0
}
Log 'started'
$pending = $listener.GetContextAsync()
while ([DateTime]::UtcNow -lt $deadline) {
    if (-not $pending.Wait(200)) { continue }
    $context = $pending.Result
    $pending = $listener.GetContextAsync()
    Log "request $($context.Request.HttpMethod) $($context.Request.Url.AbsolutePath) token=$($context.Request.Headers['X-Probe-Token']) peer=$($context.Request.RemoteEndPoint)"
    $body = [Text.Encoding]::ASCII.GetBytes($Tag)
    $context.Response.ContentLength64 = $body.Length
    $context.Response.OutputStream.Write($body, 0, $body.Length)
    $context.Response.Close()
}
$listener.Close()
