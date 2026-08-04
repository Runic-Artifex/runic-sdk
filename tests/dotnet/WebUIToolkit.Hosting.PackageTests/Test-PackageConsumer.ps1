[CmdletBinding()]
param(
    [string] $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,
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
$desktopProject = Join-Path $repositoryRoot 'src\WebUIToolkit.Desktop\WebUIToolkit.Desktop.csproj'

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

dotnet pack $desktopProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Desktop failed.' }

dotnet pack $abstractionsProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.Abstractions failed.' }

dotnet pack $hostingProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting failed.' }

dotnet pack $genericHostProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.GenericHost failed.' }

dotnet pack $buildProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.Build failed.' }

dotnet pack $mvvmProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.MVVM failed.' }

dotnet pack $webUiProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.WebUi failed.' }

dotnet pack $generatorsProject --configuration Release --output $feedDirectory --no-restore -m:1 `
    -p:PackageVersion=1.0.0
if ($LASTEXITCODE -ne 0) { throw 'Packing WebUIToolkit.Hosting.Generators failed.' }

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
$desktopPackage = Join-Path $feedDirectory 'WebUIToolkit.Desktop.1.0.0.nupkg'
Assert-PackageEntry $desktopPackage 'lib/net10.0/WebUIToolkit.Desktop.dll'
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

dotnet restore $consumerProject --no-cache
if ($LASTEXITCODE -ne 0) { throw 'Package consumer restore failed.' }

dotnet build $consumerProject --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Portable release build failed.' }

dotnet run --project $consumerProject --configuration Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Managed package-consumer scenarios failed.' }

if ($SkipNativeAot) {
    Write-Host 'Hosting managed package-consumer verification passed; Native AOT was explicitly skipped.'
    return
}

dotnet publish $consumerProject --configuration Release --runtime $RuntimeIdentifier `
    -p:PublishAot=true `
    -p:PublishTrimmed=true `
    -p:TrimMode=full `
    -p:IlcTreatWarningsAsErrors=true `
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

Write-Host 'Hosting package-consumer verification passed.'
}
finally {
    $env:NUGET_PACKAGES = $originalNuGetPackages
}
