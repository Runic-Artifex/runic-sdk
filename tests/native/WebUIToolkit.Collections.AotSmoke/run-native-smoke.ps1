[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectDirectory = $PSScriptRoot
$configuration = 'Release'
$repositoryRoot = (Resolve-Path (Join-Path $projectDirectory '..\..')).Path
$smokeProject = Join-Path $projectDirectory 'WebUIToolkit.Collections.AotSmoke.csproj'
$shippingProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Collections\WebUIToolkit.Collections.csproj'
$smokeLock = Join-Path $projectDirectory 'packages.lock.json'
$shippingLock = Join-Path $repositoryRoot 'src\WebUIToolkit.Collections\packages.lock.json'
$aotLock = Join-Path $projectDirectory 'obj\aot.packages.lock.json'

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-HostRuntimeIdentifier {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    if ($IsWindows -and $architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
        return 'win-x64'
    }

    if ($IsLinux -and $architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
        return 'linux-x64'
    }

    if ($IsMacOS -and $architecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
        return 'osx-arm64'
    }

    throw "This host ($([System.Runtime.InteropServices.RuntimeInformation]::OSDescription), $architecture) is not in the smoke matrix."
}

function Assert-PortableLock {
    param([string] $Path)

    $lock = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    $targets = @($lock.dependencies.PSObject.Properties.Name)
    if ($targets.Count -ne 1 -or $targets[0] -ne 'net10.0') {
        throw "Committed lock '$Path' is not portable: dependency targets were [$($targets -join ', ')]."
    }
}

$hostRuntimeIdentifier = Get-HostRuntimeIdentifier
if ([string]::IsNullOrEmpty($RuntimeIdentifier)) {
    $RuntimeIdentifier = $hostRuntimeIdentifier
}
elseif ($RuntimeIdentifier -ne $hostRuntimeIdentifier) {
    throw "Native AOT is host-toolchain-specific. Requested '$RuntimeIdentifier' on '$hostRuntimeIdentifier'. Run this smoke on a matching host."
}

Assert-PortableLock $smokeLock
Assert-PortableLock $shippingLock
$smokeLockHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $smokeLock).Hash
$shippingLockHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $shippingLock).Hash

Invoke-DotNet restore $shippingProject '--locked-mode'
Invoke-DotNet build $shippingProject '-c' $configuration '--no-restore'
Invoke-DotNet restore $smokeProject '--locked-mode'
Invoke-DotNet run '--project' $smokeProject '-c' $configuration '--no-restore'

Invoke-DotNet restore $smokeProject '-r' $RuntimeIdentifier '--disable-parallel' '-p:PublishAot=true' '-p:PublishTrimmed=true' "-p:NuGetLockFilePath=$aotLock" '-p:RestoreLockedMode=false'

$publishDirectory = Join-Path $projectDirectory "obj\native\$RuntimeIdentifier\publish"
Invoke-DotNet publish $smokeProject '-c' $configuration '-r' $RuntimeIdentifier '--no-restore' '-p:PublishAot=true' '-p:PublishTrimmed=true' "-p:NuGetLockFilePath=$aotLock" '-o' $publishDirectory

$executableName = if ($IsWindows) { 'WebUIToolkit.Collections.AotSmoke.exe' } else { 'WebUIToolkit.Collections.AotSmoke' }
$executable = Join-Path $publishDirectory $executableName
& $executable
if ($LASTEXITCODE -ne 0) {
    throw "Native smoke executable failed with exit code $LASTEXITCODE."
}

# Restore the portable assets after the RID-specific publish and prove that neither
# committed lock was rewritten as a side effect of native restore.
Invoke-DotNet restore $smokeProject '--locked-mode'
Assert-PortableLock $smokeLock
Assert-PortableLock $shippingLock
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $smokeLock).Hash -ne $smokeLockHash -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $shippingLock).Hash -ne $shippingLockHash) {
    throw 'A committed portable lock changed during the native smoke.'
}

Write-Host "Native-AOT smoke and portable-lock verification: PASS ($RuntimeIdentifier)"
