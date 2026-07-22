[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'candidate-manifest.json') | ConvertFrom-Json
$corpus = Get-Content -Raw -LiteralPath (Join-Path $root 'corpus/grammar.json') | ConvertFrom-Json

if ($manifest.schemaVersion -ne 'webuitoolkit.cli.evaluation/1') { throw 'Unexpected evaluation schema.' }
if ($manifest.protocolIdentity -ne 'webuitoolkit.cli/1') { throw 'Protocol identity drift.' }
if ($manifest.outputEnvironmentVariable -ne 'WEBUITOOLKIT_CLI_OUTPUT') { throw 'Output-variable identity drift.' }
if (@($manifest.candidates).Count -ne 5) { throw 'The required five candidates are not present.' }
if (@($corpus.cases).Count -lt 30) { throw 'Portable corpus is unexpectedly small.' }
if (@($corpus.cases.id | Select-Object -Unique).Count -ne @($corpus.cases).Count) { throw 'Duplicate grammar case IDs.' }
if (Get-ChildItem -Recurse -File -LiteralPath $root | Where-Object Name -eq 'packages.lock.json') { throw 'Evaluation must not retain package lock files.' }

$ownedText = Get-ChildItem -Recurse -File -LiteralPath $root |
    Where-Object FullName -ne $PSCommandPath |
    ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }
if ($ownedText -match 'CsWebUi\.CommandLine|CSWEBUI_CLI_OUTPUT|cswebui\.cli/1') { throw 'Draft command-line identity leaked into evaluation artifacts.' }

Write-Output "Validated $(@($corpus.cases).Count) grammar cases, five candidates, stable identities, and no retained lock file."
