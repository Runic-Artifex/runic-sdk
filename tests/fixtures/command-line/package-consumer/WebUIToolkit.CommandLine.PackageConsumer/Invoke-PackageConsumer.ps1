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
    'wut-cli-consumer-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $runRoot 'feed'
$consumerDirectory = Join-Path $runRoot 'consumer'
$packageCache = Join-Path $runRoot 'packages'
$publishDirectory = Join-Path $runRoot "publish/$RuntimeIdentifier"
$consumerProject = Join-Path $consumerDirectory 'Consumer.csproj'
$aotLock = Join-Path $consumerDirectory 'obj/aot.packages.lock.json'

$ownedProjects = @(
    (Join-Path $repositoryRoot 'src/WebUIToolkit.CommandLine.Abstractions/WebUIToolkit.CommandLine.Abstractions.csproj'),
    (Join-Path $repositoryRoot 'src/WebUIToolkit.CommandLine/WebUIToolkit.CommandLine.csproj'),
    (Join-Path $repositoryRoot 'src/WebUIToolkit.CommandLine.Hosting/WebUIToolkit.CommandLine.Hosting.csproj'),
    (Join-Path $repositoryRoot 'src/WebUIToolkit.Hosting.Abstractions/WebUIToolkit.Hosting.Abstractions.csproj'),
    (Join-Path $repositoryRoot 'src/WebUIToolkit.CommandLine.Processes/WebUIToolkit.CommandLine.Processes.csproj')
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

    Invoke-DotNet @('restore', $ownedProject, '--locked-mode')
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
      <package pattern="WebUIToolkit.*" />
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

    Invoke-DotNet @('restore', $consumerProject, '--force-evaluate')
    Invoke-DotNet @('restore', $consumerProject, '--locked-mode')
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
        '-p:IlcTreatWarningsAsErrors=true',
        "-p:NuGetLockFilePath=$aotLock",
        '-p:RestoreLockedMode=false'
    )

    $nativeExecutableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
        'WebUIToolkit.CommandLine.PackageConsumer.exe'
    } else {
        'WebUIToolkit.CommandLine.PackageConsumer'
    }
    $nativeExecutable = Join-Path $publishDirectory $nativeExecutableName
    if (-not (Test-Path -LiteralPath $nativeExecutable -PathType Leaf)) {
        throw "Native AOT package consumer was not produced at $nativeExecutable."
    }

    & $nativeExecutable
    if ($LASTEXITCODE -ne 0) {
        throw "Native AOT package consumer failed with exit code $LASTEXITCODE."
    }

    Invoke-DotNet @('restore', $consumerProject, '--locked-mode')

    $portableLock = Get-Content -LiteralPath (Join-Path $consumerDirectory 'packages.lock.json') -Raw
    if ($portableLock -match 'net10\.0/' -or $portableLock -match 'runtimeTargets') {
        throw 'The package consumer portable lock contains RID-specific restore state.'
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
