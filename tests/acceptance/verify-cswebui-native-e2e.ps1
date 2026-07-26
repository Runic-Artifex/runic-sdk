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
        throw 'The native CsWebUi end-to-end gate currently supports Linux and Windows.'
    }

    $RuntimeIdentifier = "$platform-$architecture"
}

function Invoke-NativeBrowserGate(
    [string]$RelativeProject,
    [string]$AssemblyName,
    [string[]]$ExecutableArguments
) {
    $project = Join-Path $repositoryRoot $RelativeProject
    $projectDirectory = Split-Path -Parent $project
    $portableLock = Join-Path $projectDirectory 'packages.lock.json'
    if (-not (Test-Path -LiteralPath $portableLock -PathType Leaf)) {
        throw "The portable lock file is missing: $RelativeProject"
    }

    $portableLockHash = (Get-FileHash -LiteralPath $portableLock -Algorithm SHA256).Hash
    $aotLock = Join-Path $projectDirectory 'obj/native-e2e.packages.lock.json'
    $publishDirectory = Join-Path $projectDirectory "obj/native-e2e-publish/$RuntimeIdentifier"

    Write-Host "Publishing native browser gate: $RelativeProject ($RuntimeIdentifier)."
    dotnet restore $project --locked-mode `
        -p:RuntimeIdentifier= `
        -p:RuntimeIdentifiers= `
        -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw "Portable locked restore failed: $RelativeProject"
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
        -p:WebUIToolkitBuildMode=Verification `
        -p:PublishDir=$publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Native-AOT publish failed: $RelativeProject"
    }

    $executableName = if ($RuntimeIdentifier.StartsWith(
        'win-',
        [System.StringComparison]::OrdinalIgnoreCase)) {
        "$AssemblyName.exe"
    }
    else {
        $AssemblyName
    }
    $executable = Join-Path $publishDirectory $executableName
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Native executable was not produced: $executable"
    }

    & $executable @ExecutableArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The native browser roundtrip failed: $RelativeProject"
    }

    $currentPortableLockHash = (Get-FileHash -LiteralPath $portableLock -Algorithm SHA256).Hash
    if ($currentPortableLockHash -ne $portableLockHash) {
        throw "Native-AOT verification changed the portable lock file: $RelativeProject"
    }
}

Push-Location $repositoryRoot
try {
    Invoke-NativeBrowserGate `
        'tests/WebUIToolkit.Hosting.CsWebUi.NativeE2E/WebUIToolkit.Hosting.CsWebUi.NativeE2E.csproj' `
        'WebUIToolkit.Hosting.CsWebUi.NativeE2E' `
        @()
    Invoke-NativeBrowserGate `
        'samples/SimpleTodo/SimpleTodo.csproj' `
        'WebUIToolkit.Samples.SimpleTodo' `
        @('--browser-smoke-test')
}
finally {
    Pop-Location
}
