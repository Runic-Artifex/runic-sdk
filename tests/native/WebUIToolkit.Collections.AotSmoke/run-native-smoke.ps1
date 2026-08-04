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

$hostRuntimeIdentifier = Get-HostRuntimeIdentifier
if ([string]::IsNullOrEmpty($RuntimeIdentifier)) {
    $RuntimeIdentifier = $hostRuntimeIdentifier
}
elseif ($RuntimeIdentifier -ne $hostRuntimeIdentifier) {
    throw "Native AOT is host-toolchain-specific. Requested '$RuntimeIdentifier' on '$hostRuntimeIdentifier'. Run this smoke on a matching host."
}

Invoke-DotNet restore $shippingProject
Invoke-DotNet build $shippingProject '-c' $configuration '--no-restore'
Invoke-DotNet restore $smokeProject
Invoke-DotNet run '--project' $smokeProject '-c' $configuration '--no-restore'

Invoke-DotNet restore $smokeProject '-r' $RuntimeIdentifier '--disable-parallel' '-p:PublishAot=true' '-p:PublishTrimmed=true'

$publishDirectory = Join-Path $projectDirectory "obj\native\$RuntimeIdentifier\publish"
Invoke-DotNet publish $smokeProject '-c' $configuration '-r' $RuntimeIdentifier '--no-restore' '-p:PublishAot=true' '-p:PublishTrimmed=true' '-o' $publishDirectory

$executableName = if ($IsWindows) { 'WebUIToolkit.Collections.AotSmoke.exe' } else { 'WebUIToolkit.Collections.AotSmoke' }
$executable = Join-Path $publishDirectory $executableName
& $executable
if ($LASTEXITCODE -ne 0) {
    throw "Native smoke executable failed with exit code $LASTEXITCODE."
}

Write-Host "Native-AOT smoke verification: PASS ($RuntimeIdentifier)"
