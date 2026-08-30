[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,

    [string] $PackageVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'runic-cli-consumer-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $runRoot 'feed'
$consumerDirectory = Join-Path $runRoot 'consumer'
$packageCache = Join-Path $runRoot 'packages'
$publishDirectory = Join-Path $runRoot "publish/$RuntimeIdentifier"
$consumerProject = Join-Path $consumerDirectory 'Consumer.csproj'

$ownedProjects = @(
    (Join-Path $repositoryRoot 'src/Runic.CommandLine/Runic.CommandLine.csproj'),
    (Join-Path $repositoryRoot 'src/Runic.CommandLine.Processes/Runic.CommandLine.Processes.csproj'),
    (Join-Path $repositoryRoot 'src/Runic.CommandLine.Testing/Runic.CommandLine.Testing.csproj')
)

function Invoke-DotNet {
    param([Parameter(Mandatory, Position = 0, ValueFromRemainingArguments)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $feed, $consumerDirectory, $packageCache, $publishDirectory | Out-Null

foreach ($ownedProject in $ownedProjects) {
    if (-not (Test-Path -LiteralPath $ownedProject -PathType Leaf)) {
        throw "Owned package project does not exist: $ownedProject"
    }

    Invoke-DotNet @('restore', $ownedProject)
    Invoke-DotNet @(
        'pack', $ownedProject,
        '--configuration', $Configuration,
        '--output', $feed,
        '--no-restore',
        "-p:PackageVersion=$PackageVersion"
    )
}

$projectTemplate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Consumer.csproj.in') -Raw
$projectTemplate.Replace('@PACKAGE_VERSION@', $PackageVersion) |
    Set-Content -LiteralPath $consumerProject -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs.in') -Destination (Join-Path $consumerDirectory 'Program.cs')

$escapedFeed = [System.Security.SecurityElement]::Escape($feed)
$nugetConfiguration = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="owned-command-line-feed" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="owned-command-line-feed">
      <package pattern="Runic.CommandLine" />
      <package pattern="Runic.CommandLine.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="Microsoft.*" />
      <package pattern="runtime.*" />
      <package pattern="System.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
$nugetConfiguration | Set-Content -LiteralPath (Join-Path $consumerDirectory 'NuGet.Config') -Encoding utf8NoBOM

$previousPackageCache = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = $packageCache

    Invoke-DotNet @('restore', $consumerProject)
    Invoke-DotNet @('build', $consumerProject, '--configuration', $Configuration, '--no-restore')
    Invoke-DotNet @('run', '--project', $consumerProject, '--configuration', $Configuration, '--no-build')

    Invoke-DotNet @(
        'publish', $consumerProject,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $publishDirectory,
        '-p:PublishAot=true',
        '-p:PublishTrimmed=true',
        '-p:TrimMode=full',
        '-p:IlcTreatWarningsAsErrors=true'
    )

    $nativeExecutableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
        'Runic.CommandLine.PackageConsumer.exe'
    } else {
        'Runic.CommandLine.PackageConsumer'
    }
    $nativeExecutable = Join-Path $publishDirectory $nativeExecutableName
    if (-not (Test-Path -LiteralPath $nativeExecutable -PathType Leaf)) {
        throw "Native AOT package consumer was not produced at $nativeExecutable."
    }

    & $nativeExecutable
    if ($LASTEXITCODE -ne 0) {
        throw "Native AOT package consumer failed with exit code $LASTEXITCODE."
    }

    Write-Host "Package consumer passed from isolated feed: $feed"
    Write-Host "Native AOT package consumer passed: $nativeExecutable"
}
finally {
    $env:NUGET_PACKAGES = $previousPackageCache
    if (Test-Path -LiteralPath $runRoot) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
