# Another process for the http.sys URL isolation checks (windows-httpsys-isolation.ps1), started as a standard user. For
# each request file <id>.prefixes that a check puts in -Directory (one URL prefix per line), it tries to register each
# prefix with an HttpListener of its own and appends to <id>.log: "registered <prefix>" or "refused <prefix> <error>",
# then "ready", then "request <method> <path>" for each request it receives, until <id>.stop appears.
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$sessions = @{}
$done = @{}
function Log([string]$id, [string]$line) { Add-Content -LiteralPath (Join-Path $Directory "$id.log") -Value $line -Encoding utf8 }
while ($true) {
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.prefixes') {
        $id = $file.BaseName
        if ($sessions.ContainsKey($id) -or $done.ContainsKey($id)) { continue }
        $listeners = @()
        foreach ($prefix in Get-Content -LiteralPath $file.FullName) {
            if (-not $prefix) { continue }
            $listener = [Net.HttpListener]::new()
            $listener.Prefixes.Add($prefix)
            try {
                $listener.Start()
                $listeners += [pscustomobject]@{ Listener = $listener; Pending = $listener.GetContextAsync() }
                Log $id "registered $prefix"
            }
            catch {
                $e = $_.Exception
                while ($e -and $e -isnot [Net.HttpListenerException]) { $e = $e.InnerException }
                Log $id "refused $prefix $($e.ErrorCode)"
            }
        }
        Log $id "user $([Security.Principal.WindowsIdentity]::GetCurrent().Name) pid $PID"
        Log $id 'ready'
        $sessions[$id] = $listeners
    }
    foreach ($id in @($sessions.Keys)) {
        foreach ($entry in $sessions[$id]) {
            if (-not $entry.Pending.IsCompleted) { continue }
            try {
                $context = $entry.Pending.Result
                $entry.Pending = $entry.Listener.GetContextAsync()
                Log $id "request $($context.Request.HttpMethod) $($context.Request.Url.AbsolutePath)"
                $context.Response.StatusCode = 200
                $context.Response.Close()
            }
            catch { $entry.Pending = $entry.Listener.GetContextAsync() }
        }
        if (Test-Path -LiteralPath (Join-Path $Directory "$id.stop")) {
            foreach ($entry in $sessions[$id]) { $entry.Listener.Close() }
            Log $id 'stopped'
            $sessions.Remove($id)
            $done[$id] = $true
        }
    }
    Start-Sleep -Milliseconds 20
}
