[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'WebUIToolkit.slnx'
$exclusionsPath = Join-Path $PSScriptRoot 'solution-exclusions.txt'

function ConvertTo-RepositoryPath([string]$path) {
    return $path.Replace('\', '/').TrimStart('./')
}

$excluded = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $exclusionsPath) {
    $candidate = $line.Trim()
    if ($candidate.Length -gt 0 -and -not $candidate.StartsWith('#')) {
        [void]$excluded.Add((ConvertTo-RepositoryPath $candidate))
    }
}

$expected = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($rootName in @('src', 'tests', 'samples', 'tools', 'templates')) {
    $root = Join-Path $repositoryRoot $rootName
    if (-not (Test-Path -LiteralPath $root)) { continue }

    foreach ($project in Get-ChildItem -LiteralPath $root -Filter '*.csproj' -Recurse -File) {
        $relative = ConvertTo-RepositoryPath ([System.IO.Path]::GetRelativePath($repositoryRoot, $project.FullName))
        if (-not $excluded.Contains($relative)) {
            [void]$expected.Add($relative)
        }
    }
}

[xml]$solution = Get-Content -LiteralPath $solutionPath -Raw
$actual = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($node in $solution.SelectNodes('//Project')) {
    $relative = ConvertTo-RepositoryPath $node.Path
    if (-not $actual.Add($relative)) {
        throw "Solution contains a duplicate project: $relative"
    }
}

$missing = @($expected | Where-Object { -not $actual.Contains($_) } | Sort-Object)
$extra = @($actual | Where-Object { -not $expected.Contains($_) } | Sort-Object)
if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
    $message = @('WebUIToolkit.slnx does not match the canonical project set.')
    if ($missing.Count -gt 0) { $message += "Missing: $($missing -join ', ')" }
    if ($extra.Count -gt 0) { $message += "Unexpected: $($extra -join ', ')" }
    throw ($message -join [Environment]::NewLine)
}

Write-Host "Solution completeness check passed for $($actual.Count) projects."
