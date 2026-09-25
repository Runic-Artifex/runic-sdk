param(
    [string] $OutputKey = "ordinary",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $BuildArguments
)

$ErrorActionPreference = "Stop"
$sdkRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$fixture = Join-Path $sdkRoot "tests/fixtures/application/PostMvvmDiscovery/PostMvvmDiscovery.csproj"
# A fixture regression may supply a deterministic private owner for its checked
# TypeScript import. Normal invocations always create a new owner session.
$owner = if ($env:RUNIC_POST_MVVM_BUILD_OWNER) {
    $env:RUNIC_POST_MVVM_BUILD_OWNER
} else {
    [Guid]::NewGuid().ToString("N")
}
$properties = @(
    "-p:RunicPostMvvmDiscoveryBuildOwner=$owner",
    "-p:RunicPostMvvmDiscoveryOwnerDriver=true",
    "-p:RunicPostMvvmDiscoveryOutputKey=$OutputKey"
)

foreach ($argument in $BuildArguments) {
    if ($argument -match '^(?:-p:|/p:|--property:)(?:OutputPath|IntermediateOutputPath|BaseOutputPath|BaseIntermediateOutputPath|MSBuildProjectExtensionsPath|ProjectAssetsFile)=') {
        [Console]::Error.WriteLine("RUNICPM010: The internal post-MVVM discovery fixture does not accept global output, intermediate, or restore path overrides because they bypass its build-owner isolation.")
        exit 2
    }
}

& dotnet restore $fixture --nologo -m:1 /nr:false @properties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet build $fixture --no-restore --nologo -m:1 /nr:false @properties @BuildArguments
exit $LASTEXITCODE
