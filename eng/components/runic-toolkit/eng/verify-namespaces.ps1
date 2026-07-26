[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$ownedRoots = @('src', 'tests', 'samples', 'web', 'protocol', 'spec')
$sourceExtensions = @(
    '.cs', '.csproj', '.props', '.targets', '.json', '.ts', '.tsx', '.js', '.mjs',
    '.md', '.cwhtml', '.cshtml', '.razor', '.nuspec', '.xml', '.yaml', '.yml'
)
$retiredPatterns = @(
    # Reject the former owned root identity without rejecting the external
    # CsWebUi package, namespace, types, or an explicitly named adapter.
    '\bnamespace\s+CsWebUi(?:[.;])',
    '<(?:AssemblyName|RootNamespace|PackageId)>CsWebUi(?:[.<])',
    '\bCsWebUi\.(?:Collections|CommandLine|DependencyNotices|Hosting|MVVM|TextResources)\b',
    '\bCSWEBUI_',
    '\bcswebui\.(?:cli|mvvm)',
    '\bcs-webui-mvvm(?:-[a-z]+)?\b',
    '@cswebui/'
)
$violations = @()

foreach ($ownedRoot in $ownedRoots) {
    $path = Join-Path $repositoryRoot $ownedRoot
    if (-not (Test-Path -LiteralPath $path)) {
        continue
    }

    $sourceFiles = Get-ChildItem -LiteralPath $path -Recurse -File |
        Where-Object {
            $_.Extension -in $sourceExtensions -and
            $_.FullName -notmatch '[\\/](?:node_modules|bin|obj|dist|coverage|artifacts)[\\/]'
        }

    foreach ($retiredPattern in $retiredPatterns) {
        $violations += $sourceFiles | Select-String -Pattern $retiredPattern -CaseSensitive
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_.ToString() }
    throw 'Owned implementation paths contain the retired CsWebUi identity.'
}

Write-Host 'Namespace identity check passed.'
