[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Feed,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $AotRid,

    [string] $AotSupportFeed
)

$ErrorActionPreference = 'Stop'
$sampleRoot = $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $sampleRoot '../../..')).Path
$verifierProject = Join-Path $repositoryRoot 'tests/WebUIToolkit.DependencyNotices.Packaging.Tests/WebUIToolkit.DependencyNotices.Packaging.Tests.csproj'
$resolvedFeed = (Resolve-Path $Feed).Path

$arguments = @(
    '--feed', $resolvedFeed,
    '--version', $Version,
    '--repository-root', $repositoryRoot
)

if ($AotRid) {
    $arguments += @('--aot-rid', $AotRid)
}

if ($AotSupportFeed) {
    $arguments += @('--aot-support-feed', (Resolve-Path $AotSupportFeed).Path)
}

dotnet restore $verifierProject --locked-mode --source $resolvedFeed
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project $verifierProject --configuration Release --no-restore -- @arguments
exit $LASTEXITCODE
