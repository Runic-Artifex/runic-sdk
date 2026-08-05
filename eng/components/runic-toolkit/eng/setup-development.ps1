[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packageDirectory = Join-Path $repositoryRoot 'artifacts/local-development/packages'
$toolProject = Join-Path $repositoryRoot (
    'tools/dotnet-runic-toolkit/RunicToolkit.DotNet.RunicToolkit.csproj')
$templateProject = Join-Path $repositoryRoot (
    'templates/RunicToolkit.Templates/RunicToolkit.Templates.csproj')
$templatePackage = Join-Path $packageDirectory (
    'RunicToolkit.Templates.1.0.0-beta.1.nupkg')

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

    Write-Host 'Packing the repository-local RunicToolkit development command.'
    Invoke-DotNet -Arguments @(
        'pack',
        $toolProject,
        '--configuration', $Configuration,
        '--output', $packageDirectory,
        '-p:PackageVersion=1.0.0',
        '-p:NuGetAudit=false')

    if (-not (Test-Path -LiteralPath $templateProject -PathType Leaf)) {
        throw "The template pack project is missing: $templateProject"
    }

    Write-Host 'Packing the repository-local RunicToolkit templates.'
    Invoke-DotNet -Arguments @(
        'pack',
        $templateProject,
        '--configuration', $Configuration,
        '--output', $packageDirectory,
        '-p:NuGetAudit=false')

    Write-Host 'Restoring the repository-local dotnet tool manifest.'
    Invoke-DotNet -Arguments @(
        'tool',
        'restore',
        '--add-source', $packageDirectory,
        '--ignore-failed-sources')

    if (-not (Test-Path -LiteralPath $templatePackage -PathType Leaf)) {
        throw "The template package was not produced: $templatePackage"
    }

    Write-Host 'Installing the repository-local dotnet new templates.'
    Invoke-DotNet -Arguments @('new', 'install', $templatePackage, '--force')

    Write-Host ''
    Write-Host 'RunicToolkit development setup is ready.'
    Write-Host 'Run: dotnet runic-toolkit doctor <PROJECT>'
    Write-Host 'Then: dotnet runic-toolkit dev <PROJECT>'
}
finally {
    Pop-Location
}
