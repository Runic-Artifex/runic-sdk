[CmdletBinding()]
param(
    [switch] $NoRestore,
    [switch] $NoTest
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$developmentProperties = @('-p:WebUIToolkitBuildMode=Development')

Push-Location $repositoryRoot
try {
    if (-not $NoRestore) {
        dotnet restore WebUIToolkit.slnx @developmentProperties
        if ($LASTEXITCODE -ne 0) { throw 'Development restore failed.' }
    }

    dotnet build WebUIToolkit.slnx --no-restore @developmentProperties
    if ($LASTEXITCODE -ne 0) { throw 'Development build failed.' }

    if (-not $NoTest) {
        dotnet test WebUIToolkit.slnx --no-build --no-restore @developmentProperties
        if ($LASTEXITCODE -ne 0) { throw 'Development tests failed.' }
    }
}
finally {
    Pop-Location
}
