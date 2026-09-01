param(
    [Parameter(Mandatory)][string]$PackageVersion,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$RepositoryCommit,
    [string]$RunicDesktopVersion = $env:RunicDesktopVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RunicDesktopVersion)) {
    $RunicDesktopVersion = "1.0.0-preview.1"
}

$repositoryUrl = "https://github.com/Runic-Artifex/runic-assets"
$expectedPackages = [ordered]@{
    "Runic.Assets" = @{}
    "Runic.Assets.AspNetCore" = @{
        "Runic.Assets" = $PackageVersion
    }
    "Runic.Assets.Desktop" = @{
        "Runic.Assets" = $PackageVersion
        "Runic.Desktop" = $RunicDesktopVersion
    }
}

function Read-Nuspec {
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith(".nuspec") })
        if ($entries.Count -ne 1) {
            throw "Expected one nuspec in '$Path', found $($entries.Count)."
        }

        $reader = [System.IO.StreamReader]::new($entries[0].Open())
        try { return [xml]$reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Read-RequiredMetadataValue {
    param(
        [Parameter(Mandatory)][xml]$Document,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$PackagePath
    )

    $node = $Document.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='$Name']")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Package '$PackagePath' is missing required '$Name' metadata."
    }

    return $node.InnerText
}

$resolvedDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$actualPackages = @(Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter "*.nupkg")
if ($actualPackages.Count -ne $expectedPackages.Count) {
    throw "Expected $($expectedPackages.Count) packages, found $($actualPackages.Count)."
}

foreach ($packageId in $expectedPackages.Keys) {
    $packagePath = Join-Path $resolvedDirectory "$packageId.$PackageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Expected package was not produced: $packagePath"
    }

    $document = Read-Nuspec -Path $packagePath
    if ((Read-RequiredMetadataValue -Document $document -Name "id" -PackagePath $packagePath) -ne $packageId) {
        throw "Package '$packagePath' has an unexpected package id."
    }
    if ((Read-RequiredMetadataValue -Document $document -Name "version" -PackagePath $packagePath) -ne $PackageVersion) {
        throw "Package '$packagePath' has an unexpected package version."
    }
    if ((Read-RequiredMetadataValue -Document $document -Name "license" -PackagePath $packagePath) -ne "MIT") {
        throw "Package '$packagePath' must use the MIT license expression."
    }

    $repository = $document.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='repository']")
    if ($null -eq $repository -or
        $repository.GetAttribute("type") -ne "git" -or
        $repository.GetAttribute("url") -ne $repositoryUrl -or
        $repository.GetAttribute("commit") -ne $RepositoryCommit) {
        throw "Package '$packagePath' does not contain the expected repository provenance."
    }

    $actualDependencies = @($document.SelectNodes("//*[local-name()='dependency']"))
    $expectedDependencies = $expectedPackages[$packageId]
    if ($actualDependencies.Count -ne $expectedDependencies.Count) {
        throw "Expected $($expectedDependencies.Count) dependencies in '$packagePath', found $($actualDependencies.Count)."
    }

    foreach ($dependency in $actualDependencies) {
        $dependencyId = $dependency.GetAttribute("id")
        $dependencyVersion = $dependency.GetAttribute("version")
        if (-not $expectedDependencies.ContainsKey($dependencyId) -or
            $expectedDependencies[$dependencyId] -ne $dependencyVersion) {
            throw "Unexpected dependency '$dependencyId' version '$dependencyVersion' in '$packagePath'."
        }
    }

    if ($packageId -eq "Runic.Assets") {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
            $requiredEntries = @(
                "buildTransitive/Runic.Assets.targets",
                "tools/net10.0/Runic.Assets.Packer.dll",
                "tools/net10.0/Runic.Assets.dll",
                "tools/net10.0/Runic.CommandLine.dll",
                "tools/net10.0/Runic.Assets.Packer.deps.json",
                "tools/net10.0/Runic.Assets.Packer.runtimeconfig.json"
            )

            foreach ($entryName in $requiredEntries) {
                if ($entryNames -notcontains $entryName) {
                    throw "Package '$packagePath' is missing required build asset '$entryName'."
                }
            }
        }
        finally { $archive.Dispose() }

        $toolRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("runic-assets-package-tool-" + [guid]::NewGuid().ToString("N"))
        try {
            [System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $toolRoot)
            $toolInput = Join-Path $toolRoot "fixture"
            [System.IO.Directory]::CreateDirectory($toolInput) | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $toolInput "index.html"), "<main>packaged tool</main>")
            $toolPath = Join-Path $toolRoot "tools/net10.0/Runic.Assets.Packer.dll"
            $toolArchive = Join-Path $toolInput "output.runic-assets"
            & dotnet $toolPath $toolInput $toolArchive
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $toolArchive -PathType Leaf)) {
                throw "Packaged Runic Assets packer did not produce an archive."
            }

            [byte[]]$firstArchive = [System.IO.File]::ReadAllBytes($toolArchive)
            & dotnet $toolPath $toolInput $toolArchive
            if ($LASTEXITCODE -ne 0 -or -not [System.Collections.StructuralComparisons]::StructuralEqualityComparer.Equals($firstArchive, [System.IO.File]::ReadAllBytes($toolArchive))) {
                throw "Packaged Runic Assets packer is not reproducible when its output is inside the source directory."
            }
        }
        finally {
            if (Test-Path -LiteralPath $toolRoot) {
                Remove-Item -LiteralPath $toolRoot -Recurse -Force
            }
        }
    }
}

Write-Host "Verified $($expectedPackages.Count) Runic Assets package artifacts for $PackageVersion."
