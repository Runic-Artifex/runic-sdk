[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot 'tests'

$projects = Get-ChildItem -LiteralPath $testRoot -Filter '*.csproj' -Recurse -File |
    Sort-Object FullName |
    Where-Object { $_.BaseName -notmatch 'AotSmoke' } |
    Where-Object {
        [xml]$projectDocument = Get-Content -LiteralPath $_.FullName -Raw
        $outputTypes = @($projectDocument.Project.PropertyGroup.OutputType)
        $outputTypes -contains 'Exe'
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
