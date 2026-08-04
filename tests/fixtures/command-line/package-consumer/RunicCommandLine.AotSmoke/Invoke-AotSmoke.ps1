[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$project = Join-Path $PSScriptRoot 'RunicCommandLine.AotSmoke.csproj'
$publishDirectory = Join-Path $PSScriptRoot "artifacts/$Configuration/$RuntimeIdentifier/native"

function Invoke-DotNet {
    param([Parameter(Mandatory, Position = 0, ValueFromRemainingArguments)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Invoke-DotNet @('restore', $project)
Invoke-DotNet @('build', $project, '--configuration', $Configuration, '--no-restore')
Invoke-DotNet @('run', '--project', $project, '--configuration', $Configuration, '--no-build')

Invoke-DotNet @(
    'publish', $project,
    '--configuration', $Configuration,
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--output', $publishDirectory,
    '-p:PublishAot=true'
)

$nativeExecutableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
    'RunicCommandLine.AotSmoke.exe'
} else {
    'RunicCommandLine.AotSmoke'
}
$nativeExecutable = Join-Path $publishDirectory $nativeExecutableName
if (-not (Test-Path -LiteralPath $nativeExecutable -PathType Leaf)) {
    throw "Native AOT executable was not produced at $nativeExecutable."
}

& $nativeExecutable
if ($LASTEXITCODE -ne 0) {
    throw "Native AOT smoke executable failed with exit code $LASTEXITCODE."
}

Write-Host "Native AOT smoke passed: $nativeExecutable"
