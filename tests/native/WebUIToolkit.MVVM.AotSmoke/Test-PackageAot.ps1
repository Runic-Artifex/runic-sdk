[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $RuntimeIdentifier,

    [ValidateNotNullOrEmpty()]
    [string] $PackageVersion = "1.0.0-aot-smoke"
)

$ErrorActionPreference = "Stop"
$smokeDirectory = $PSScriptRoot
$repositoryDirectory = (Resolve-Path (Join-Path $smokeDirectory "../..")).Path
$libraryProject = Join-Path $repositoryDirectory "src/WebUIToolkit.MVVM/WebUIToolkit.MVVM.csproj"
$smokeProject = Join-Path $smokeDirectory "WebUIToolkit.MVVM.AotSmoke.csproj"
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("webuitoolkit-mvvm-aot-" + [Guid]::NewGuid().ToString("N"))
$packageDirectory = Join-Path $temporaryDirectory "package"
$publishDirectory = Join-Path $temporaryDirectory "publish"
$consumerLockFile = Join-Path $temporaryDirectory "consumer.packages.lock.json"
$packagesPath = Join-Path $temporaryDirectory "packages"
$intermediatePath = (Join-Path $temporaryDirectory "obj") + [System.IO.Path]::DirectorySeparatorChar
$outputPath = (Join-Path $temporaryDirectory "bin") + [System.IO.Path]::DirectorySeparatorChar

try {
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

    & dotnet pack $libraryProject --configuration Release --output $packageDirectory "-p:PackageVersion=$PackageVersion"
    if ($LASTEXITCODE -ne 0) { throw "Packing WebUIToolkit.MVVM failed." }

    & dotnet publish $smokeProject --configuration Release --runtime $RuntimeIdentifier --output $publishDirectory "-p:MvvmUsePackage=true" "-p:MvvmPackageVersion=$PackageVersion" "-p:MvvmNativeAot=true" "-p:NuGetLockFilePath=$consumerLockFile" "-p:RestorePackagesPath=$packagesPath" "-p:RestoreAdditionalProjectSources=$packageDirectory" "-p:BaseIntermediateOutputPath=$intermediatePath" "-p:BaseOutputPath=$outputPath" "-p:RestoreDisableParallel=true" -maxcpucount:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "Native-AOT package-consumer publish failed." }

    $windowsExecutable = Join-Path $publishDirectory "WebUIToolkit.MVVM.AotSmoke.exe"
    $executablePath = if (Test-Path -LiteralPath $windowsExecutable) {
        $windowsExecutable
    }
    else {
        Join-Path $publishDirectory "WebUIToolkit.MVVM.AotSmoke"
    }
    & $executablePath
    if ($LASTEXITCODE -ne 0) { throw "Published Native-AOT smoke executable failed." }

    Write-Host "Package-consumer Native-AOT smoke passed for $RuntimeIdentifier."
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
