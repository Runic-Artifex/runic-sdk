param(
    [Parameter(Mandatory)][string]$PackageVersion,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$RepositoryCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryUrl = "https://github.com/Runic-Artifex/runic-assets"
$expectedPackages = [ordered]@{
    "RunicAssets" = @{}
    "RunicAssets.CsWebUi" = @{
        "CsWebUi" = "2.5.0-beta.4.4"
        "RunicAssets" = $PackageVersion
    }
    "RunicAssets.AspNetCore" = @{
        "RunicAssets" = $PackageVersion
    }
    "RunicAssets.RunicToolkit" = @{
        "RunicAssets" = $PackageVersion
        "RunicToolkit.Hosting.Abstractions" = "[0.1.0-preview.30.1]"
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

    if ($packageId -eq "RunicAssets") {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
            $requiredEntries = @(
                "buildTransitive/RunicAssets.targets",
                "tools/net10.0/RunicAssets.Packer.dll",
                "tools/net10.0/RunicAssets.Packer.deps.json",
                "tools/net10.0/RunicAssets.Packer.runtimeconfig.json"
            )

            foreach ($entryName in $requiredEntries) {
                if ($entryNames -notcontains $entryName) {
                    throw "Package '$packagePath' is missing required build asset '$entryName'."
                }
            }
        }
        finally { $archive.Dispose() }
    }
}

Write-Host "Verified $($expectedPackages.Count) Runic Assets package artifacts for $PackageVersion."
