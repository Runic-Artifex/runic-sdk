[CmdletBinding()]
param(
    [string]$RuntimeIdentifier,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if (-not $env:CSWEBUI_NATIVE_LIBRARY -or
    -not (Test-Path -LiteralPath $env:CSWEBUI_NATIVE_LIBRARY -PathType Leaf)) {
    throw 'CSWEBUI_NATIVE_LIBRARY must identify the pinned native WebUI library.'
}

if (-not $env:WEBUI_BROWSER_PATH -or
    -not (Test-Path -LiteralPath $env:WEBUI_BROWSER_PATH -PathType Leaf)) {
    throw 'WEBUI_BROWSER_PATH must identify the pinned Chromium executable.'
}

if (-not $RuntimeIdentifier) {
    $architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        ([System.Runtime.InteropServices.Architecture]::X64) { 'x64' }
        ([System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' }
        default {
            throw "Unsupported Native-AOT architecture: $(
                [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
        }
    }

    $platform = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux)) {
        'linux'
    }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        'win'
    }
    else {
        throw 'The Todo Native-AOT browser gate currently supports Linux and Windows.'
    }

    $RuntimeIdentifier = "$platform-$architecture"
}

$projects = @(
    'Todo.React',
    'Todo.Vue',
    'Todo.Svelte',
    'Todo.Angular'
)
$gateCount = 0
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Push-Location $repositoryRoot
try {
    foreach ($name in $projects) {
        $relativeProject = "samples/$name/$name.csproj"
        $project = Join-Path $repositoryRoot $relativeProject
        $projectDirectory = Split-Path -Parent $project
        $portableLock = Join-Path $projectDirectory 'packages.lock.json'
        if (-not (Test-Path -LiteralPath $portableLock -PathType Leaf)) {
            throw "The portable lock file is missing: $relativeProject"
        }

        $portableLockHash = (Get-FileHash -LiteralPath $portableLock -Algorithm SHA256).Hash
        $aotLock = Join-Path $projectDirectory 'obj/todo-native-aot.packages.lock.json'
        $publishDirectory = Join-Path $projectDirectory "obj/todo-native-aot/$RuntimeIdentifier"

        Write-Host "Publishing Todo Native-AOT gate: $relativeProject ($RuntimeIdentifier)."
        dotnet restore $project `
            --locked-mode `
            -p:RuntimeIdentifier= `
            -p:RuntimeIdentifiers= `
            -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) {
            throw "Portable locked restore failed: $relativeProject"
        }

        dotnet publish $project `
            --configuration $Configuration `
            --runtime $RuntimeIdentifier `
            --self-contained true `
            -p:PublishAot=true `
            -p:PublishTrimmed=true `
            -p:TrimMode=full `
            -p:IlcTreatWarningsAsErrors=true `
            -p:NuGetLockFilePath=$aotLock `
            -p:NuGetAudit=false `
            -p:RestoreLockedMode=false `
            -p:RunicToolkitBuildMode=Verification `
            -p:PublishDir=$publishDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Native-AOT publish failed: $relativeProject"
        }

        $executableName = if ($RuntimeIdentifier.StartsWith(
            'win-',
            [System.StringComparison]::OrdinalIgnoreCase)) {
            "RunicToolkit.Samples.$name.exe"
        }
        else {
            "RunicToolkit.Samples.$name"
        }
        $executable = Join-Path $publishDirectory $executableName
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Native executable was not produced: $executable"
        }

        $demoCases = @(
            [PSCustomObject]@{
                Name = 'Simple'
                Arguments = [string[]]@('--browser-smoke-test')
            },
            [PSCustomObject]@{
                Name = 'Advanced'
                Arguments = [string[]]@('--advanced', '--browser-smoke-test')
            }
        )
        foreach ($demoCase in $demoCases) {
            & $executable @($demoCase.Arguments)
            if ($LASTEXITCODE -ne 0) {
                throw "$name $($demoCase.Name) Native-AOT browser gate failed."
            }
            $gateCount++
        }

        $currentPortableLockHash = (Get-FileHash -LiteralPath $portableLock -Algorithm SHA256).Hash
        if ($currentPortableLockHash -ne $portableLockHash) {
            throw "Native-AOT verification changed the portable lock file: $relativeProject"
        }
    }

    $stopwatch.Stop()
    Write-Host (
        "PASS: {0} Native-AOT Todo browser variants passed in {1:n1}s." -f
        $gateCount,
        $stopwatch.Elapsed.TotalSeconds)
}
finally {
    Pop-Location
}
