[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$ownedRoots = @('src', 'tests', 'tools', 'templates', 'web', 'protocol')
$sourceExtensions = @(
    '.cs', '.csproj', '.props', '.targets', '.json', '.ts', '.tsx', '.js', '.mjs',
    '.svelte', '.md', '.nuspec', '.xml', '.yaml', '.yml'
)
$retiredPatterns = @(
    '\bWebUIToolkit\b',
    '\bWEBUITOOLKIT\b',
    '\bwebuitoolkit\b',
    '\bWUT(?:MVVM|HOST|DEV|FE)\b',
    '\bwut(?:mvvm|-bindings)\b',
    '\bdata-wut\b',
    '\bRunicToolkit\.(?:Assets|CommandLine|Flow|Translations)\b',
    '__runic-toolkit'
)
$violations = @()

foreach ($ownedRoot in $ownedRoots) {
    $path = Join-Path $repositoryRoot $ownedRoot
    if (-not (Test-Path -LiteralPath $path)) { continue }

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
    throw 'Owned implementation paths contain a retired Toolkit identity.'
}

$lockFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter packages.lock.json |
    Where-Object { $_.FullName -notmatch '[\\/](?:node_modules|bin|obj)[\\/]' })
if ($lockFiles.Count -gt 0) {
    throw "Product repositories do not commit NuGet lock files: $($lockFiles.FullName -join ', ')"
}

Write-Host 'Toolkit identity and NuGet policy checks passed.'
