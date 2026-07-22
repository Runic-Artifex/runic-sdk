[CmdletBinding()]
param(
    [string] $RuntimeIdentifier = 'win-x64',
    [switch] $RefreshPortableLock
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDirectory = $PSScriptRoot
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory '..\..'))
$packagesDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '.packages'))
$feedDirectory = [System.IO.Path]::GetFullPath((Join-Path $packagesDirectory 'hosting-wave-b'))
$consumerCache = [System.IO.Path]::GetFullPath((Join-Path $packagesDirectory 'hosting-package-tests\nuget'))
$consumerProject = Join-Path $projectDirectory 'WebUIToolkit.Hosting.PackageTests.csproj'
$abstractionsProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Hosting.Abstractions\WebUIToolkit.Hosting.Abstractions.csproj'
$hostingProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Hosting\WebUIToolkit.Hosting.csproj'
$buildProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Hosting.Build\WebUIToolkit.Hosting.Build.csproj'

$packagesPrefix = $packagesDirectory + [System.IO.Path]::DirectorySeparatorChar
if (-not $consumerCache.StartsWith($packagesPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear package cache outside '$packagesDirectory'."
}

if (Test-Path -LiteralPath $consumerCache) {
    Remove-Item -LiteralPath $consumerCache -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $feedDirectory | Out-Null

function ConvertTo-DeterministicPackage {
    param([Parameter(Mandatory)] [string] $PackagePath)

    $normalizedPath = $PackagePath + '.normalized'
    if ([System.IO.File]::Exists($normalizedPath)) {
        [System.IO.File]::Delete($normalizedPath)
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    $inputArchive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        foreach ($entry in $inputArchive.Entries) {
            $entryStream = $entry.Open()
            try {
                $content = [System.IO.MemoryStream]::new()
                try {
                    $entryStream.CopyTo($content)
                    $bytes = $content.ToArray()
                }
                finally {
                    $content.Dispose()
                }
            }
            finally {
                $entryStream.Dispose()
            }

            $name = $entry.FullName
            if ($name -eq '_rels/.rels') {
                $relationships = [System.Text.Encoding]::UTF8.GetString($bytes)
                $relationships = [regex]::Replace(
                    $relationships,
                    'package/services/metadata/core-properties/[^"/]+\.psmdcp',
                    'package/services/metadata/core-properties/core.psmdcp')
                $relationships = [regex]::Replace(
                    $relationships,
                    '(<Relationship Type="http://schemas\.microsoft\.com/packaging/2010/07/manifest"[^>]* Id=")[^"]+("\s*/>)',
                    '$1RManifest$2')
                $relationships = [regex]::Replace(
                    $relationships,
                    '(<Relationship Type="http://schemas\.openxmlformats\.org/package/2006/relationships/metadata/core-properties"[^>]* Id=")[^"]+("\s*/>)',
                    '$1RCoreProperties$2')
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($relationships)
            }
            elseif ($name -like 'package/services/metadata/core-properties/*.psmdcp') {
                $name = 'package/services/metadata/core-properties/core.psmdcp'
            }

            $entries.Add([pscustomobject]@{ Name = $name; Content = $bytes })
        }
    }
    finally {
        $inputArchive.Dispose()
    }

    $outputArchive = [System.IO.Compression.ZipFile]::Open(
        $normalizedPath,
        [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $fixedTimestamp = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
        foreach ($item in @($entries | Sort-Object -Property Name)) {
            $outputEntry = $outputArchive.CreateEntry(
                $item.Name,
                [System.IO.Compression.CompressionLevel]::Optimal)
            $outputEntry.LastWriteTime = $fixedTimestamp
            $outputStream = $outputEntry.Open()
            try {
                $outputStream.Write($item.Content, 0, $item.Content.Length)
            }
            finally {
                $outputStream.Dispose()
            }
        }
    }
    finally {
        $outputArchive.Dispose()
    }

    [System.IO.File]::Move($normalizedPath, $PackagePath, $true)
}

$stableRevision = '0000000000000000000000000000000000000000'
dotnet pack $abstractionsProject --configuration Release --output $feedDirectory --no-restore `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.Abstractions failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.Hosting.Abstractions.1.0.0.nupkg')

dotnet pack $hostingProject --configuration Release --output $feedDirectory --no-restore `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.Hosting.1.0.0.nupkg')

dotnet pack $buildProject --configuration Release --output $feedDirectory --no-restore `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.Build failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.Hosting.Build.1.0.0.nupkg')

function Assert-PackageEntry {
    param(
        [Parameter(Mandatory)] [string] $PackagePath,
        [Parameter(Mandatory)] [string] $EntryName
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        if ($null -eq $archive.GetEntry($EntryName)) {
            throw "Package '$PackagePath' does not contain '$EntryName'."
        }
    }
    finally {
        $archive.Dispose()
    }
}

$abstractionsPackage = Join-Path $feedDirectory 'WebUIToolkit.Hosting.Abstractions.1.0.0.nupkg'
$hostingPackage = Join-Path $feedDirectory 'WebUIToolkit.Hosting.1.0.0.nupkg'
$buildPackage = Join-Path $feedDirectory 'WebUIToolkit.Hosting.Build.1.0.0.nupkg'
Assert-PackageEntry $abstractionsPackage 'lib/net10.0/WebUIToolkit.Hosting.Abstractions.dll'
Assert-PackageEntry $hostingPackage 'lib/net10.0/WebUIToolkit.Hosting.dll'
Assert-PackageEntry $buildPackage 'lib/net10.0/WebUIToolkit.Hosting.Build.dll'
Assert-PackageEntry $abstractionsPackage 'lib/net10.0/WebUIToolkit.Hosting.Abstractions.xml'
Assert-PackageEntry $hostingPackage 'lib/net10.0/WebUIToolkit.Hosting.xml'
Assert-PackageEntry $buildPackage 'lib/net10.0/WebUIToolkit.Hosting.Build.xml'
Assert-PackageEntry $abstractionsPackage 'README.md'
Assert-PackageEntry $hostingPackage 'README.md'
Assert-PackageEntry $buildPackage 'README.md'

function Read-PackageNuspec {
    param([Parameter(Mandatory)] [string] $PackagePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [System.StringComparison]::Ordinal) })
        if ($entries.Count -ne 1) {
            throw "Expected exactly one nuspec in '$PackagePath'."
        }

        $entry = $entries[0]
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { return [xml] $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally {
        $archive.Dispose()
    }
}

$hostingNuspec = Read-PackageNuspec $hostingPackage
$abstractionsNuspec = Read-PackageNuspec $abstractionsPackage
$buildNuspec = Read-PackageNuspec $buildPackage

function Get-PackageDependencies {
    param([Parameter(Mandatory)] [xml] $Nuspec)

    $result = [System.Collections.Generic.List[object]]::new()
    foreach ($group in @($Nuspec.package.metadata.dependencies.group)) {
        if ($null -ne $group -and $null -ne $group.PSObject.Properties['dependency']) {
            foreach ($dependency in @($group.dependency)) {
                $result.Add($dependency)
            }
        }
    }

    return $result.ToArray()
}

$hostingDependencies = @(Get-PackageDependencies $hostingNuspec)
$abstractionsDependencies = @(Get-PackageDependencies $abstractionsNuspec)
$buildDependencies = @(Get-PackageDependencies $buildNuspec)
if (-not ($hostingDependencies | Where-Object { $_.id -eq 'WebUIToolkit.Hosting.Abstractions' })) {
    throw 'WebUIToolkit.Hosting must depend on WebUIToolkit.Hosting.Abstractions.'
}
if ($abstractionsDependencies | Where-Object { $_.id -like 'WebUIToolkit.Hosting*' }) {
    throw 'WebUIToolkit.Hosting.Abstractions must not depend on another Hosting package.'
}
if (-not ($buildDependencies | Where-Object { $_.id -eq 'WebUIToolkit.Hosting.Abstractions' })) {
    throw 'WebUIToolkit.Hosting.Build must depend on WebUIToolkit.Hosting.Abstractions.'
}
if ($buildDependencies | Where-Object { $_.id -eq 'WebUIToolkit.Hosting' }) {
    throw 'WebUIToolkit.Hosting.Build must not depend on the Hosting runtime package.'
}

if ($RefreshPortableLock) {
    dotnet restore $consumerProject --force-evaluate -p:RestoreLockedMode=false
    if ($LASTEXITCODE -ne 0) { throw 'Refreshing the portable package lock failed.' }
}

dotnet restore $consumerProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Portable locked restore failed.' }

dotnet build $consumerProject --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Portable release build failed.' }

dotnet run --project $consumerProject --configuration Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Managed package-consumer scenarios failed.' }

$portableLock = Get-Content (Join-Path $projectDirectory 'packages.lock.json') -Raw | ConvertFrom-Json
if (@($portableLock.dependencies.PSObject.Properties.Name | Where-Object { $_ -match '/' }).Count -ne 0) {
    throw 'The committed package lock contains a RID-specific dependency section.'
}

function Assert-PackageLockHash {
    param(
        [Parameter(Mandatory)] [string] $PackagePath,
        [Parameter(Mandatory)] [string] $ExpectedHash
    )

    $actualHash = [Convert]::ToBase64String(
        [System.Security.Cryptography.SHA512]::HashData(
            [System.IO.File]::ReadAllBytes($PackagePath)))
    if ($actualHash -ne $ExpectedHash) {
        throw "Package '$PackagePath' does not match its portable lock contentHash."
    }
}

$lockedDependencies = $portableLock.dependencies.'net10.0'
Assert-PackageLockHash $hostingPackage $lockedDependencies.'WebUIToolkit.Hosting'.contentHash
Assert-PackageLockHash $abstractionsPackage $lockedDependencies.'WebUIToolkit.Hosting.Abstractions'.contentHash
Assert-PackageLockHash $buildPackage $lockedDependencies.'WebUIToolkit.Hosting.Build'.contentHash

dotnet publish $consumerProject --configuration Release --runtime $RuntimeIdentifier `
    -p:PublishAot=true `
    -p:NuGetLockFilePath=obj/aot.packages.lock.json `
    -p:RestoreLockedMode=false
if ($LASTEXITCODE -ne 0) { throw 'Native-AOT package-consumer publish failed.' }

$nativeExecutable = Join-Path $projectDirectory "bin\Release\net10.0\$RuntimeIdentifier\publish\WebUIToolkit.Hosting.PackageTests.exe"
if (-not (Test-Path -LiteralPath $nativeExecutable -PathType Leaf)) {
    throw "Native package-consumer executable was not produced at '$nativeExecutable'."
}

& $nativeExecutable
if ($LASTEXITCODE -ne 0) { throw 'Native package-consumer scenarios failed.' }

dotnet restore $consumerProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Final portable locked restore failed.' }

Write-Host 'Hosting package-consumer verification passed.'
