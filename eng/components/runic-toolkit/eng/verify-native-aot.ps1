[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$exclusionsPath = Join-Path $PSScriptRoot 'solution-exclusions.txt'

$projects = Get-Content -LiteralPath $exclusionsPath |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') -and $_ -match 'AotSmoke' } |
    ForEach-Object { Get-Item -LiteralPath (Join-Path $repositoryRoot $_) } |
    Sort-Object FullName

if ($projects.Count -eq 0) {
    throw 'No Native-AOT smoke projects were registered.'
}

Push-Location $repositoryRoot
try {
    foreach ($project in $projects) {
        $relativePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $project.FullName)
        Write-Host "Publishing Native-AOT smoke: $relativePath ($RuntimeIdentifier)"

        dotnet restore $project.FullName --locked-mode -p:RuntimeIdentifier= -p:RuntimeIdentifiers=
        if ($LASTEXITCODE -ne 0) { throw "Portable locked restore failed: $relativePath" }

        $projectDirectory = $project.DirectoryName
        $aotLock = Join-Path $projectDirectory 'obj/aot.packages.lock.json'
        $publishDirectory = Join-Path $projectDirectory "obj/aot-publish/$RuntimeIdentifier"
        dotnet publish $project.FullName --configuration $Configuration --runtime $RuntimeIdentifier --self-contained true `
            -p:PublishAot=true `
            -p:NuGetLockFilePath=$aotLock `
            -p:RestoreLockedMode=false `
            -p:PublishDir=$publishDirectory
        if ($LASTEXITCODE -ne 0) { throw "Native-AOT publish failed: $relativePath" }

        $executableName = if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
            "$($project.BaseName).exe"
        }
        else {
            $project.BaseName
        }
        $executable = Join-Path $publishDirectory $executableName
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Native executable was not produced: $executable"
        }

        & $executable
        if ($LASTEXITCODE -ne 0) { throw "Native-AOT smoke failed: $relativePath" }
    }

    git diff --exit-code -- ':(glob)**/packages.lock.json'
    if ($LASTEXITCODE -ne 0) {
        throw 'A Native-AOT publish changed a committed portable lock file.'
    }
}
finally {
    Pop-Location
}
