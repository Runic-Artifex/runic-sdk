param(
    [string]$Configuration = "Release",
    [switch]$SkipNativeAot,
    [string]$RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).Path
$scratch = Join-Path $PSScriptRoot ("obj/package-replay-" + [Guid]::NewGuid().ToString("N"))
$feed = Join-Path $scratch "feed"
$consumerDirectory = Join-Path $scratch "consumer"
$consumer = Join-Path $consumerDirectory "PackageConsumer.csproj"
$consumerCache = Join-Path $scratch "package-cache"
$config = Join-Path $scratch "NuGet.config"
$originalNuGetPackages = $env:NUGET_PACKAGES

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Resolve-CachedPackage {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Version
    )

    $relativePath = Join-Path (Join-Path $Id.ToLowerInvariant() $Version) "$($Id.ToLowerInvariant()).$Version.nupkg"
    $cacheRoots = @(
        (Join-Path $root ".packages/nuget"),
        $originalNuGetPackages
    )

    $globalPackagesLine = (& dotnet nuget locals global-packages --list | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $globalPackagesLine.StartsWith("global-packages:")) {
        $cacheRoots += $globalPackagesLine.Substring($globalPackagesLine.IndexOf(":") + 1).Trim()
    }

    foreach ($cacheRoot in $cacheRoots | Where-Object { $_ } | Select-Object -Unique) {
        $candidate = Join-Path $cacheRoot $relativePath
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "$Id $Version is not available in a repository or configured NuGet package cache."
}

function Assert-PackageDependencies {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][hashtable]$Expected
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $nuspec = @($archive.Entries | Where-Object { $_.FullName.EndsWith(".nuspec") })
        if ($nuspec.Count -ne 1) {
            throw "Expected one nuspec in '$Path', found $($nuspec.Count)."
        }

        $reader = [System.IO.StreamReader]::new($nuspec[0].Open())
        try {
            [xml]$document = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $dependencies = @($document.SelectNodes("//*[local-name()='dependency']"))
        if ($dependencies.Count -ne $Expected.Count) {
            throw "Expected $($Expected.Count) dependencies in '$Path', found $($dependencies.Count)."
        }

        foreach ($dependency in $dependencies) {
            $id = $dependency.GetAttribute("id")
            $version = $dependency.GetAttribute("version")
            if (-not $Expected.ContainsKey($id) -or $Expected[$id] -ne $version) {
                throw "Unexpected dependency '$id' version '$version' in '$Path'."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $feed, $consumerDirectory, $consumerCache | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Consumer/PackageConsumer.csproj") -Destination $consumerDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Consumer/Program.cs") -Destination $consumerDirectory
    Copy-Item -LiteralPath (Resolve-CachedPackage -Id "CommunityToolkit.Mvvm" -Version "8.4.2") -Destination $feed

    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="isolated-communitytoolkit-feed" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="isolated-communitytoolkit-feed">
      <package pattern="WebUIToolkit.*" />
      <package pattern="CommunityToolkit.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="Microsoft.*" />
      <package pattern="runtime.*" />
      <package pattern="System.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -Encoding UTF8 $config

    Invoke-DotNet @("pack", "-c", $Configuration, "--no-restore", "-p:PackageVersion=0.0.0-local",
        "-p:ContinuousIntegrationBuild=true", "-p:WebUIToolkitBuildMode=Verification",
        "-o", $feed, (Join-Path $root "src/WebUIToolkit.MVVM/WebUIToolkit.MVVM.csproj"))
    Invoke-DotNet @("pack", "-c", $Configuration, "--no-restore", "-p:PackageVersion=0.0.0-local",
        "-p:ContinuousIntegrationBuild=true", "-p:WebUIToolkitBuildMode=Verification",
        "-o", $feed, (Join-Path $root "src/WebUIToolkit.MVVM.CommunityToolkit/WebUIToolkit.MVVM.CommunityToolkit.csproj"))

    $runtimePackage = Join-Path $feed "WebUIToolkit.MVVM.0.0.0-local.nupkg"
    $adapterPackage = Join-Path $feed "WebUIToolkit.MVVM.CommunityToolkit.0.0.0-local.nupkg"
    Assert-PackageDependencies -Path $runtimePackage -Expected @{}
    Assert-PackageDependencies -Path $adapterPackage -Expected @{
        "CommunityToolkit.Mvvm" = "[8.4.2]"
        "WebUIToolkit.MVVM" = "0.0.0-local"
    }

    $env:NUGET_PACKAGES = $consumerCache
    Invoke-DotNet @("restore", $consumer, "-m:1", "--disable-parallel", "--configfile", $config,
        "--no-cache", "-p:NuGetAudit=false", "-p:RestorePackagesWithLockFile=false",
        "-p:CommunityToolkitAdapterPackageVersion=0.0.0-local")
    Invoke-DotNet @("run", "-c", $Configuration, "--no-restore",
        "-p:CommunityToolkitAdapterPackageVersion=0.0.0-local", "--project", $consumer)

    if (-not $SkipNativeAot) {
        $publishDirectory = Join-Path $consumerDirectory "obj/aot-publish/$RuntimeIdentifier"
        Invoke-DotNet @("restore", $consumer, "--runtime", $RuntimeIdentifier, "--configfile", $config,
            "-m:1", "--disable-parallel", "-p:PublishAot=true", "-p:PublishTrimmed=true",
            "-p:TrimMode=full", "-p:NuGetAudit=false", "-p:RestorePackagesWithLockFile=false",
            "-p:CommunityToolkitAdapterPackageVersion=0.0.0-local")
        Invoke-DotNet @("publish", $consumer, "-c", $Configuration, "--runtime", $RuntimeIdentifier,
            "--self-contained", "true", "--no-restore", "--output", $publishDirectory,
            "-p:PublishAot=true", "-p:PublishTrimmed=true", "-p:TrimMode=full",
            "-p:IlcTreatWarningsAsErrors=true", "-p:CommunityToolkitAdapterPackageVersion=0.0.0-local")

        $nativeExecutableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
            'WebUIToolkit.MVVM.CommunityToolkit.PackageConsumer.exe'
        } else {
            'WebUIToolkit.MVVM.CommunityToolkit.PackageConsumer'
        }
        $nativeExecutable = Join-Path $publishDirectory $nativeExecutableName
        if (-not (Test-Path -LiteralPath $nativeExecutable -PathType Leaf)) {
            throw "Native-AOT package consumer was not produced at '$nativeExecutable'."
        }
        & $nativeExecutable
        if ($LASTEXITCODE -ne 0) {
            throw "Native-AOT package consumer failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    $env:NUGET_PACKAGES = $originalNuGetPackages
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
