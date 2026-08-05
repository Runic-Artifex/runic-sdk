[CmdletBinding()]
param(
    [switch] $NoRestore,
    [switch] $NoTest
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$developmentProperties = @('-p:RunicToolkitBuildMode=Development')

Push-Location $repositoryRoot
try {
    if (-not $NoRestore) {
        dotnet restore RunicToolkit.slnx @developmentProperties
        if ($LASTEXITCODE -ne 0) { throw 'Development restore failed.' }
    }

    dotnet build RunicToolkit.slnx --no-restore @developmentProperties
    if ($LASTEXITCODE -ne 0) { throw 'Development build failed.' }

    if (-not $NoTest) {
        & (Join-Path $PSScriptRoot 'run-contract-tests.ps1') -Configuration Debug
        if ($LASTEXITCODE -ne 0) { throw 'Development contract tests failed.' }
    }
}
finally {
    Pop-Location
}
