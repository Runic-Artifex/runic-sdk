param(
    [Parameter(Mandatory)][string]$PackageVersion,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$RepositoryCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryUrl = 'https://github.com/Runic-Artifex/runic-toolkit'
$expectedPackages = @(
    'Runic.Application',
    'Runic.Application.Hosting',
    'Runic.Application.Desktop',
    'Runic.Application.Testing',
    'Runic.Application.Bridge',
    'dotnet-runic',
    'Runic.Application.Templates'
)
$expectedExternalDependencies = @{
    'Runic.CommandLine' = if ($env:RunicCommandLinePackageVersion) { $env:RunicCommandLinePackageVersion } else { '1.0.0-preview.1' }
    'Runic.Assets' = if ($env:RunicAssetsPackageVersion) { $env:RunicAssetsPackageVersion } else { '1.0.0-preview.1' }
    'Runic.Translations' = if ($env:RunicTranslationsPackageVersion) { $env:RunicTranslationsPackageVersion } else { '1.0.0-preview.1' }
    'Runic.Desktop' = if ($env:RunicDesktopPackageVersion) { $env:RunicDesktopPackageVersion } else { '1.0.0-preview.1' }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-Nuspec([string]$Path) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
        if ($entries.Count -ne 1) { throw "Expected one nuspec in '$Path'." }
        $reader = [System.IO.StreamReader]::new($entries[0].Open())
        try { return [xml]$reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Read-Metadata([xml]$Document, [string]$Name, [string]$Path) {
    $node = $Document.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='$Name']")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Package '$Path' is missing '$Name' metadata."
    }
    return $node.InnerText
}

$resolvedDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$actualPackages = @(Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter '*.nupkg')
if ($actualPackages.Count -ne $expectedPackages.Count) {
    throw "Expected $($expectedPackages.Count) packages, found $($actualPackages.Count)."
}

foreach ($packageId in $expectedPackages) {
    $packagePath = Join-Path $resolvedDirectory "$packageId.$PackageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Expected package was not produced: $packagePath"
    }

    $document = Read-Nuspec $packagePath
    if ((Read-Metadata $document 'id' $packagePath) -ne $packageId) {
        throw "Package '$packagePath' has an unexpected id."
    }
    if ((Read-Metadata $document 'version' $packagePath) -ne $PackageVersion) {
        throw "Package '$packagePath' has an unexpected version."
    }
    if ((Read-Metadata $document 'license' $packagePath) -ne 'MIT') {
        throw "Package '$packagePath' must use the MIT license expression."
    }

    $repository = $document.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='repository']")
    if ($null -eq $repository -or
        $repository.GetAttribute('type') -ne 'git' -or
        $repository.GetAttribute('url') -ne $repositoryUrl -or
        $repository.GetAttribute('commit') -ne $RepositoryCommit) {
        throw "Package '$packagePath' does not contain the expected repository provenance."
    }

    foreach ($dependency in @($document.SelectNodes("//*[local-name()='dependency'][starts-with(@id, 'Runic.Application') or @id='dotnet-runic']") )) {
        $dependencyVersion = $dependency.GetAttribute('version')
        if ($dependencyVersion -notin @($PackageVersion, "[$PackageVersion]")) {
            throw "Internal dependency '$($dependency.GetAttribute('id'))' in '$packagePath' does not start at $PackageVersion (found '$dependencyVersion')."
        }
    }

    foreach ($dependency in @($document.SelectNodes("//*[local-name()='dependency']"))) {
        $dependencyId = $dependency.GetAttribute('id')
        if (-not $expectedExternalDependencies.ContainsKey($dependencyId)) { continue }
        $expectedVersion = $expectedExternalDependencies[$dependencyId]
        $dependencyVersion = $dependency.GetAttribute('version')
        if ($dependencyVersion -notin @($expectedVersion, "[$expectedVersion]")) {
            throw "External dependency '$dependencyId' in '$packagePath' must select $expectedVersion (found '$dependencyVersion')."
        }
    }
}

Write-Host "Verified $($expectedPackages.Count) local Runic Application package artifacts for $PackageVersion."
