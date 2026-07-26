[CmdletBinding()]
param(
    [string] $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,
    [switch] $RefreshPortableLock,
    [switch] $SkipNativeAot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDirectory = $PSScriptRoot
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory '..\..'))
$packagesDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '.packages'))
$feedDirectory = [System.IO.Path]::GetFullPath((Join-Path $packagesDirectory 'hosting-wave-c'))
$consumerCache = [System.IO.Path]::GetFullPath((Join-Path $packagesDirectory 'hosting-package-tests\nuget'))
$consumerProject = Join-Path $projectDirectory 'WebUIToolkit.Hosting.PackageTests.csproj'
$abstractionsProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Hosting.Abstractions\WebUIToolkit.Hosting.Abstractions.csproj'
$hostingProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Hosting\WebUIToolkit.Hosting.csproj'
$genericHostProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Hosting.GenericHost\WebUIToolkit.Hosting.GenericHost.csproj'
$buildProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Hosting.Build\WebUIToolkit.Hosting.Build.csproj'
$webUiProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Hosting.WebUi\WebUIToolkit.Hosting.WebUi.csproj'
$generatorsProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Hosting.Generators\WebUIToolkit.Hosting.Generators.csproj'
$mvvmProject = Join-Path $repositoryRoot 'src\WebUIToolkit.MVVM\WebUIToolkit.MVVM.csproj'

$packagesPrefix = $packagesDirectory + [System.IO.Path]::DirectorySeparatorChar
if (-not $consumerCache.StartsWith($packagesPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear package cache outside '$packagesDirectory'."
}

if (Test-Path -LiteralPath $consumerCache) {
    Remove-Item -LiteralPath $consumerCache -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $feedDirectory | Out-Null
$originalNuGetPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = $consumerCache

try {

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
dotnet pack $abstractionsProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.Abstractions failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.Hosting.Abstractions.1.0.0.nupkg')

dotnet pack $hostingProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.Hosting.1.0.0.nupkg')

dotnet pack $genericHostProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.GenericHost failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.Hosting.GenericHost.1.0.0.nupkg')

dotnet pack $buildProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.Build failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.Hosting.Build.1.0.0.nupkg')

dotnet pack $mvvmProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.MVVM failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.MVVM.1.0.0.nupkg')

dotnet pack $webUiProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.WebUi failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.Hosting.WebUi.1.0.0.nupkg')

dotnet pack $generatorsProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0 `
    -p:RepositoryCommit=$stableRevision `
    -p:SourceRevisionId=$stableRevision
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.Generators failed.' }
ConvertTo-DeterministicPackage (Join-Path $feedDirectory 'WebUIToolkit.Hosting.Generators.1.0.0.nupkg')

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
$genericHostPackage = Join-Path $feedDirectory 'WebUIToolkit.Hosting.GenericHost.1.0.0.nupkg'
$buildPackage = Join-Path $feedDirectory 'WebUIToolkit.Hosting.Build.1.0.0.nupkg'
$webUiPackage = Join-Path $feedDirectory 'WebUIToolkit.Hosting.WebUi.1.0.0.nupkg'
$generatorsPackage = Join-Path $feedDirectory 'WebUIToolkit.Hosting.Generators.1.0.0.nupkg'
$mvvmPackage = Join-Path $feedDirectory 'WebUIToolkit.MVVM.1.0.0.nupkg'
Assert-PackageEntry $abstractionsPackage 'lib/net10.0/WebUIToolkit.Hosting.Abstractions.dll'
Assert-PackageEntry $hostingPackage 'lib/net10.0/WebUIToolkit.Hosting.dll'
Assert-PackageEntry $genericHostPackage 'lib/net10.0/WebUIToolkit.Hosting.GenericHost.dll'
Assert-PackageEntry $buildPackage 'lib/net10.0/WebUIToolkit.Hosting.Build.dll'
Assert-PackageEntry $webUiPackage 'lib/net10.0/WebUIToolkit.Hosting.WebUi.dll'
Assert-PackageEntry $generatorsPackage 'lib/net10.0/WebUIToolkit.Hosting.Generators.dll'
Assert-PackageEntry $mvvmPackage 'lib/net10.0/WebUIToolkit.MVVM.dll'
Assert-PackageEntry $abstractionsPackage 'lib/net10.0/WebUIToolkit.Hosting.Abstractions.xml'
Assert-PackageEntry $hostingPackage 'lib/net10.0/WebUIToolkit.Hosting.xml'
Assert-PackageEntry $genericHostPackage 'lib/net10.0/WebUIToolkit.Hosting.GenericHost.xml'
Assert-PackageEntry $buildPackage 'lib/net10.0/WebUIToolkit.Hosting.Build.xml'
Assert-PackageEntry $webUiPackage 'lib/net10.0/WebUIToolkit.Hosting.WebUi.xml'
Assert-PackageEntry $generatorsPackage 'lib/net10.0/WebUIToolkit.Hosting.Generators.xml'
Assert-PackageEntry $buildPackage 'buildTransitive/WebUIToolkit.Hosting.Build.props'
Assert-PackageEntry $buildPackage 'buildTransitive/WebUIToolkit.Hosting.Build.targets'
Assert-PackageEntry $buildPackage 'tasks/net10.0/WebUIToolkit.Hosting.Build.dll'
Assert-PackageEntry $buildPackage 'tasks/net10.0/WebUIToolkit.Hosting.Abstractions.dll'
Assert-PackageEntry $generatorsPackage 'analyzers/dotnet/cs/WebUIToolkit.Hosting.Generators.dll'
Assert-PackageEntry $abstractionsPackage 'README.md'
Assert-PackageEntry $hostingPackage 'README.md'
Assert-PackageEntry $genericHostPackage 'README.md'
Assert-PackageEntry $buildPackage 'README.md'
Assert-PackageEntry $webUiPackage 'README.md'
Assert-PackageEntry $generatorsPackage 'README.md'

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
$genericHostNuspec = Read-PackageNuspec $genericHostPackage
$abstractionsNuspec = Read-PackageNuspec $abstractionsPackage
$buildNuspec = Read-PackageNuspec $buildPackage
$webUiNuspec = Read-PackageNuspec $webUiPackage
$generatorsNuspec = Read-PackageNuspec $generatorsPackage

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

function Test-PackageFrameworkReference {
    param(
        [Parameter(Mandatory)] [xml] $Nuspec,
        [Parameter(Mandatory)] [string] $Name
    )

    return $null -ne $Nuspec.SelectSingleNode(
        "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='frameworkReferences']/*[local-name()='group']/*[local-name()='frameworkReference'][@name='$Name']")
}

$hostingDependencies = @(Get-PackageDependencies $hostingNuspec)
$genericHostDependencies = @(Get-PackageDependencies $genericHostNuspec)
$abstractionsDependencies = @(Get-PackageDependencies $abstractionsNuspec)
$buildDependencies = @(Get-PackageDependencies $buildNuspec)
$webUiDependencies = @(Get-PackageDependencies $webUiNuspec)
$generatorDependencies = @(Get-PackageDependencies $generatorsNuspec)
if (-not ($hostingDependencies | Where-Object { $_.id -eq 'WebUIToolkit.Hosting.Abstractions' })) {
    throw 'WebUIToolkit.Hosting must depend on WebUIToolkit.Hosting.Abstractions.'
}
if (Test-PackageFrameworkReference $hostingNuspec 'Microsoft.AspNetCore.App') {
    throw 'WebUIToolkit.Hosting core must remain independent of Microsoft.AspNetCore.App.'
}
if (-not ($genericHostDependencies | Where-Object { $_.id -eq 'WebUIToolkit.Hosting' })) {
    throw 'WebUIToolkit.Hosting.GenericHost must depend inward on WebUIToolkit.Hosting.'
}
if (-not ($genericHostDependencies | Where-Object { $_.id -eq 'Microsoft.Extensions.Hosting' })) {
    throw 'WebUIToolkit.Hosting.GenericHost must declare Microsoft.Extensions.Hosting.'
}
if (Test-PackageFrameworkReference $genericHostNuspec 'Microsoft.AspNetCore.App') {
    throw 'WebUIToolkit.Hosting.GenericHost must remain independent of Microsoft.AspNetCore.App.'
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
if (-not ($webUiDependencies | Where-Object { $_.id -eq 'WebUIToolkit.Hosting' })) {
    throw 'WebUIToolkit.Hosting.WebUi must depend on WebUIToolkit.Hosting.'
}
if (-not ($webUiDependencies | Where-Object { $_.id -eq 'WebUIToolkit.MVVM' })) {
    throw 'WebUIToolkit.Hosting.WebUi must depend on WebUIToolkit.MVVM.'
}
if (-not ($webUiDependencies | Where-Object { $_.id -eq 'Microsoft.Extensions.DependencyInjection.Abstractions' })) {
    throw 'WebUIToolkit.Hosting.WebUi must declare Microsoft.Extensions.DependencyInjection.Abstractions.'
}
if (Test-PackageFrameworkReference $webUiNuspec 'Microsoft.AspNetCore.App') {
    throw 'WebUIToolkit.Hosting.WebUi must remain independent of Microsoft.AspNetCore.App.'
}
if ($generatorDependencies | Where-Object { $_.id -like 'WebUIToolkit.Hosting*' }) {
    throw 'WebUIToolkit.Hosting.Generators must not acquire a Hosting runtime dependency.'
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
Assert-PackageLockHash $genericHostPackage $lockedDependencies.'WebUIToolkit.Hosting.GenericHost'.contentHash
Assert-PackageLockHash $abstractionsPackage $lockedDependencies.'WebUIToolkit.Hosting.Abstractions'.contentHash
Assert-PackageLockHash $buildPackage $lockedDependencies.'WebUIToolkit.Hosting.Build'.contentHash
Assert-PackageLockHash $webUiPackage $lockedDependencies.'WebUIToolkit.Hosting.WebUi'.contentHash
Assert-PackageLockHash $generatorsPackage $lockedDependencies.'WebUIToolkit.Hosting.Generators'.contentHash
Assert-PackageLockHash $mvvmPackage $lockedDependencies.'WebUIToolkit.MVVM'.contentHash

if ($SkipNativeAot) {
    dotnet restore $consumerProject --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Final portable locked restore failed.' }

    Write-Host 'Hosting managed package-consumer verification passed; Native AOT was explicitly skipped.'
    return
}

dotnet publish $consumerProject --configuration Release --runtime $RuntimeIdentifier `
    -p:PublishAot=true `
    -p:PublishTrimmed=true `
    -p:TrimMode=full `
    -p:IlcTreatWarningsAsErrors=true `
    -p:NuGetLockFilePath=obj/aot.packages.lock.json `
    -p:RestoreLockedMode=false `
    -p:RestoreForceEvaluate=true
if ($LASTEXITCODE -ne 0) { throw 'Native-AOT package-consumer publish failed.' }

$nativeExecutableName = if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::Ordinal)) {
    'WebUIToolkit.Hosting.PackageTests.exe'
}
else {
    'WebUIToolkit.Hosting.PackageTests'
}
$nativeExecutable = Join-Path $projectDirectory "bin\Release\net10.0\$RuntimeIdentifier\publish\$nativeExecutableName"
if (-not (Test-Path -LiteralPath $nativeExecutable -PathType Leaf)) {
    throw "Native package-consumer executable was not produced at '$nativeExecutable'."
}

& $nativeExecutable
if ($LASTEXITCODE -ne 0) { throw 'Native package-consumer scenarios failed.' }

dotnet restore $consumerProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Final portable locked restore failed.' }

Write-Host 'Hosting package-consumer verification passed.'
}
finally {
    $env:NUGET_PACKAGES = $originalNuGetPackages
}
