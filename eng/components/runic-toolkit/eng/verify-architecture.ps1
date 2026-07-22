[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$registryPath = Join-Path $PSScriptRoot 'ownership.json'
$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json

function Get-RelativePath([string]$path) {
    return [IO.Path]::GetRelativePath($repositoryRoot, $path).Replace('\', '/')
}

function Get-PathOwner([string]$relativePath) {
    $matches = @(
        $registry.tasks | Where-Object {
            $task = $_
            @($task.paths | Where-Object { $relativePath -like $_ }).Count -gt 0
        }
    )

    if ($matches.Count -ne 1) {
        throw "Path '$relativePath' must have exactly one owner; found $($matches.Count)."
    }

    return $matches[0].name
}

$projectRoots = @('src', 'tests', 'samples', 'tools', 'benchmarks')
$projects = @()
foreach ($projectRoot in $projectRoots) {
    $path = Join-Path $repositoryRoot $projectRoot
    if (Test-Path -LiteralPath $path) {
        $projects += Get-ChildItem -LiteralPath $path -Recurse -Filter *.csproj -File
    }
}

foreach ($project in $projects) {
    $projectRelativePath = Get-RelativePath $project.FullName
    $projectOwner = Get-PathOwner $projectRelativePath
    [xml]$projectXml = Get-Content -LiteralPath $project.FullName -Raw

    foreach ($reference in @($projectXml.Project.ItemGroup.ProjectReference)) {
        if ($null -eq $reference -or [string]::IsNullOrWhiteSpace($reference.Include)) {
            continue
        }

        $targetPath = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName $reference.Include))
        $targetRelativePath = Get-RelativePath $targetPath
        if ($targetRelativePath.StartsWith('../', [StringComparison]::Ordinal)) {
            throw "Project '$projectRelativePath' references a project outside the repository."
        }

        $targetOwner = Get-PathOwner $targetRelativePath
        if ($targetOwner -ne $projectOwner) {
            throw "Cross-owner ProjectReference is forbidden: '$projectRelativePath' ($projectOwner) -> '$targetRelativePath' ($targetOwner). Use a packed handoff."
        }
    }
}

Write-Host "Architecture ownership check passed for $($projects.Count) projects."
