[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot 'tests'
$exclusionsPath = Join-Path $PSScriptRoot 'solution-exclusions.txt'
$excluded = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $exclusionsPath) {
    $candidate = $line.Trim()
    if ($candidate.Length -gt 0 -and -not $candidate.StartsWith('#')) {
        [void]$excluded.Add($candidate.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    }
}

$projects = Get-ChildItem -LiteralPath $testRoot -Filter '*.csproj' -Recurse -File |
    Sort-Object FullName |
    Where-Object {
        $relativePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)
        -not $excluded.Contains($relativePath)
    } |
    Where-Object {
        [xml]$projectDocument = Get-Content -LiteralPath $_.FullName -Raw
        $outputTypes = @($projectDocument.Project.PropertyGroup.OutputType)
        $outputTypes -contains 'Exe'
    } |
    Where-Object {
        # This is a separate environment gate. The native browser canary needs
        # the pinned cs-webui library and Chromium.
        $_.BaseName -notin @(
            'RunicToolkit.Hosting.CsWebUi.NativeE2E'
        )
    }

if ($projects.Count -eq 0) {
    throw 'No executable contract-test projects were discovered.'
}

Push-Location $repositoryRoot
try {
    foreach ($testProject in $projects) {
        $relativePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $testProject.FullName)
        Write-Host "Running executable contract tests: $relativePath"
        dotnet run --project $testProject.FullName --configuration $Configuration --no-build --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "Executable contract tests failed: $relativePath"
        }
    }
}
finally {
    Pop-Location
}
