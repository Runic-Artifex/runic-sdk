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
    Where-Object { $_ -and -not $_.StartsWith('#') -and $_ -match 'Aot(?:Smoke|Tests)' } |
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

        $projectDirectory = $project.DirectoryName
        $projectProperties = @()
        if ($project.BaseName -ceq 'WebUIToolkit.TextResources.AotTests') {
            $feed = Join-Path $projectDirectory 'obj/aot-feed'
            if (Test-Path -LiteralPath $feed) {
                Remove-Item -LiteralPath $feed -Recurse -Force
            }
            New-Item -ItemType Directory -Path $feed | Out-Null
            $runtimeProject = Join-Path $repositoryRoot `
                'src/WebUIToolkit.TextResources/WebUIToolkit.TextResources.csproj'
            dotnet pack $runtimeProject --configuration $Configuration --no-restore `
                -p:PackageVersion=1.0.0 `
                -p:ContinuousIntegrationBuild=true `
                "-p:PathMap=$repositoryRoot=/_/" `
                -p:NuGetAudit=false `
                --output $feed
            if ($LASTEXITCODE -ne 0) { throw 'Packing Text Resources for Native-AOT verification failed.' }

            $projectProperties += "-p:TextResourcesPackageFeed=$feed"
        }

        dotnet restore $project.FullName -p:RuntimeIdentifier= -p:RuntimeIdentifiers= `
            -p:NuGetAudit=false @projectProperties
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $relativePath" }

        $publishDirectory = Join-Path $projectDirectory "obj/aot-publish/$RuntimeIdentifier"
        dotnet publish $project.FullName --configuration $Configuration --runtime $RuntimeIdentifier --self-contained true `
            -p:PublishAot=true `
            -p:PublishTrimmed=true `
            -p:TrimMode=full `
            -p:IlcTreatWarningsAsErrors=true `
            -p:NuGetAudit=false `
            -p:PublishDir=$publishDirectory `
            @projectProperties
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
}
finally {
    Pop-Location
}
