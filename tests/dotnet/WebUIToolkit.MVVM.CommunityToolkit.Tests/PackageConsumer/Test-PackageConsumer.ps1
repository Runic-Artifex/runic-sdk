param(
    [string]$Configuration = "Release",
    [switch]$UpdateLock,
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
$committedConsumer = Join-Path $PSScriptRoot "Consumer"
$committedLock = Join-Path $committedConsumer "packages.lock.json"
$replayLock = Join-Path $consumerDirectory "packages.lock.json"
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

function Normalize-NuGetPackage {
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $normalizedPath = "$Path.normalized"
    $source = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $destinationStream = [System.IO.File]::Open(
            $normalizedPath,
            [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        try {
            $destination = [System.IO.Compression.ZipArchive]::new(
                $destinationStream,
                [System.IO.Compression.ZipArchiveMode]::Create,
                $true)
            try {
                $entries = [System.Collections.Generic.List[System.IO.Compression.ZipArchiveEntry]]::new()
                $entries.AddRange($source.Entries)
                $entries.Sort(
                    [System.Comparison[System.IO.Compression.ZipArchiveEntry]] {
                        param($left, $right)
                        return [StringComparer]::Ordinal.Compare($left.FullName, $right.FullName)
                    })
                foreach ($entry in $entries) {
                    if ($entry.FullName -eq "[Content_Types].xml" -or
                        $entry.FullName -eq "_rels/.rels" -or
                        $entry.FullName.StartsWith(
                            "package/services/metadata/core-properties/",
                            [StringComparison]::Ordinal)) {
                        continue
                    }

                    $normalizedEntry = $destination.CreateEntry(
                        $entry.FullName,
                        [System.IO.Compression.CompressionLevel]::Optimal)
                    $normalizedEntry.LastWriteTime =
                        [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                    # Host file-mode metadata is not part of the package contract.
                    # Fix it so Windows and Unix pack output normalizes identically.
                    $normalizedEntry.ExternalAttributes = 0
                    $input = $entry.Open()
                    $output = $normalizedEntry.Open()
                    try {
                        $input.CopyTo($output)
                    }
                    finally {
                        $output.Dispose()
                        $input.Dispose()
                    }
                }
            }
            finally {
                $destination.Dispose()
            }
        }
        finally {
            $destinationStream.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }

    Move-Item -LiteralPath $normalizedPath -Destination $Path -Force
}

function Get-NuGetContentHash {
    param([Parameter(Mandatory)][string]$Path)

    if (-not ("NuGet.Packaging.PackageArchiveReader" -as [type])) {
        $sdkVersion = (& dotnet --version | Out-String).Trim()
        $sdkLine = @(& dotnet --list-sdks) |
            Where-Object { $_.StartsWith("$sdkVersion ") } |
            Select-Object -First 1
        if (-not $sdkLine -or $sdkLine -notmatch "\[(.+)\]") {
            throw "Could not locate NuGet.Packaging.dll for SDK $sdkVersion."
        }

        $sdkPath = Join-Path $Matches[1] $sdkVersion
        foreach ($assembly in Get-ChildItem -LiteralPath $sdkPath -Filter "NuGet*.dll") {
            [Reflection.Assembly]::LoadFrom($assembly.FullName) | Out-Null
        }
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha512 = [System.Security.Cryptography.SHA512]::Create()
        try {
            $archiveHash = [Convert]::ToBase64String($sha512.ComputeHash($stream))
        }
        finally {
            $sha512.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $reader = [NuGet.Packaging.PackageArchiveReader]::new($Path)
    try {
        return $reader.GetContentHash(
            [Threading.CancellationToken]::None,
            [Func[string]] { return $archiveHash })
    }
    finally {
        $reader.Dispose()
    }
}

function Assert-PackageModeLock {
    param([Parameter(Mandatory)][string]$Path)

    $framework = (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json).dependencies."net10.0"
    $direct = @($framework.PSObject.Properties | Where-Object { $_.Value.type -eq "Direct" })
    if ($direct.Count -ne 1 -or $direct[0].Name -ne "WebUIToolkit.MVVM.CommunityToolkit") {
        throw "The consumer lock must have only WebUIToolkit.MVVM.CommunityToolkit as a direct package."
    }

    $adapter = $framework.PSObject.Properties["WebUIToolkit.MVVM.CommunityToolkit"].Value
    $toolkit = $framework.PSObject.Properties["CommunityToolkit.Mvvm"].Value
    $runtime = $framework.PSObject.Properties["WebUIToolkit.MVVM"].Value
    if ($adapter.dependencies."CommunityToolkit.Mvvm" -ne "[8.4.2]") {
        throw "The packed adapter must require exact CommunityToolkit.Mvvm [8.4.2]."
    }

    if ($toolkit.type -ne "Transitive" -or $toolkit.resolved -ne "8.4.2") {
        throw "CommunityToolkit.Mvvm 8.4.2 must be supplied transitively by the packed adapter."
    }

    if ($runtime.type -ne "Transitive" -or $runtime.resolved -ne "0.0.0-local") {
        throw "WebUIToolkit.MVVM 0.0.0-local must be supplied transitively by the packed adapter."
    }
}

function Assert-PackageContentHash {
    param(
        [Parameter(Mandatory)][string]$LockPath,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$PackagePath
    )

    $locked = (Get-Content -LiteralPath $LockPath -Raw |
        ConvertFrom-Json).dependencies."net10.0".PSObject.Properties[$PackageId].Value.contentHash
    $actual = Get-NuGetContentHash -Path $PackagePath
    if ($locked -ne $actual) {
        throw "$PackageId contentHash does not match the deterministic package bytes. Run with -UpdateLock."
    }
}

function Update-CommittedLock {
    if ($UpdateLock) {
        Copy-Item -LiteralPath $replayLock -Destination $committedLock -Force
    }
}

try {
    if (-not $UpdateLock) {
        Assert-PackageModeLock -Path $committedLock
    }
    New-Item -ItemType Directory -Path $feed, $consumerDirectory, $consumerCache | Out-Null
    Copy-Item -LiteralPath (Join-Path $committedConsumer "PackageConsumer.csproj") -Destination $consumerDirectory
    Copy-Item -LiteralPath (Join-Path $committedConsumer "Program.cs") -Destination $consumerDirectory
    Copy-Item -LiteralPath $committedLock -Destination $consumerDirectory
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
"@ | Set-Content -LiteralPath $config -Encoding UTF8

    Invoke-DotNet @("pack", "-c", $Configuration, "--no-restore", "-p:PackageVersion=0.0.0-local",
        "-p:ContinuousIntegrationBuild=true", "-p:PathMap=$root=/_/",
        "-o", $feed, (Join-Path $root "src/WebUIToolkit.MVVM/WebUIToolkit.MVVM.csproj"))
    Invoke-DotNet @("pack", "-c", $Configuration, "--no-restore", "-p:PackageVersion=0.0.0-local",
        "-p:ContinuousIntegrationBuild=true", "-p:PathMap=$root=/_/",
        "-o", $feed, (Join-Path $root "src/WebUIToolkit.MVVM.CommunityToolkit/WebUIToolkit.MVVM.CommunityToolkit.csproj"))

    $runtimePackage = Join-Path $feed "WebUIToolkit.MVVM.0.0.0-local.nupkg"
    $adapterPackage = Join-Path $feed "WebUIToolkit.MVVM.CommunityToolkit.0.0.0-local.nupkg"
    Normalize-NuGetPackage -Path $runtimePackage
    Normalize-NuGetPackage -Path $adapterPackage

    $env:NUGET_PACKAGES = $consumerCache
    if ($UpdateLock) {
        Invoke-DotNet @("restore", $consumer, "-m:1", "--disable-parallel", "--configfile", $config,
            "--force-evaluate", "-p:NuGetAudit=false", "-p:RestoreLockedMode=false",
            "-p:CommunityToolkitAdapterPackageVersion=0.0.0-local")
        Assert-PackageModeLock -Path $replayLock
        Update-CommittedLock
        Remove-Item -LiteralPath (Join-Path $consumerDirectory "obj") -Recurse -Force
        Remove-Item -LiteralPath $consumerCache -Recurse -Force
        New-Item -ItemType Directory -Path $consumerCache | Out-Null
    }

    Assert-PackageContentHash -LockPath $replayLock -PackageId "WebUIToolkit.MVVM" -PackagePath $runtimePackage
    Assert-PackageContentHash -LockPath $replayLock -PackageId "WebUIToolkit.MVVM.CommunityToolkit" -PackagePath $adapterPackage
    Assert-PackageContentHash -LockPath $replayLock -PackageId "CommunityToolkit.Mvvm" -PackagePath (
        Join-Path $feed "communitytoolkit.mvvm.8.4.2.nupkg")
    Invoke-DotNet @("restore", $consumer, "-m:1", "--disable-parallel", "--configfile", $config,
        "--locked-mode", "--no-cache", "-p:NuGetAudit=false",
        "-p:CommunityToolkitAdapterPackageVersion=0.0.0-local")
    Invoke-DotNet @("run", "-c", $Configuration, "--no-restore",
        "-p:CommunityToolkitAdapterPackageVersion=0.0.0-local", "--project", $consumer)

    if (-not $SkipNativeAot) {
        $aotLock = Join-Path $consumerDirectory 'obj/aot.packages.lock.json'
        $publishDirectory = Join-Path $consumerDirectory "obj/aot-publish/$RuntimeIdentifier"
        Invoke-DotNet @("restore", $consumer, "--runtime", $RuntimeIdentifier, "--configfile", $config,
            "-m:1", "--disable-parallel", "-p:PublishAot=true", "-p:PublishTrimmed=true",
            "-p:TrimMode=full", "-p:NuGetAudit=false", "-p:RestoreLockedMode=false",
            "-p:CommunityToolkitAdapterPackageVersion=0.0.0-local", "-p:NuGetLockFilePath=$aotLock")
        Invoke-DotNet @("publish", $consumer, "-c", $Configuration, "--runtime", $RuntimeIdentifier,
            "--self-contained", "true", "--no-restore", "--output", $publishDirectory,
            "-p:PublishAot=true", "-p:PublishTrimmed=true", "-p:TrimMode=full",
            "-p:IlcTreatWarningsAsErrors=true", "-p:CommunityToolkitAdapterPackageVersion=0.0.0-local",
            "-p:NuGetLockFilePath=$aotLock")

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
