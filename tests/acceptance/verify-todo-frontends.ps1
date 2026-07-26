[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if (-not $env:CSWEBUI_NATIVE_LIBRARY -or
    -not (Test-Path -LiteralPath $env:CSWEBUI_NATIVE_LIBRARY -PathType Leaf)) {
    throw 'CSWEBUI_NATIVE_LIBRARY must identify the pinned native WebUI library.'
}

if (-not $env:WEBUI_BROWSER_PATH -or
    -not (Test-Path -LiteralPath $env:WEBUI_BROWSER_PATH -PathType Leaf)) {
    throw 'WEBUI_BROWSER_PATH must identify the pinned Chromium executable.'
}

$projects = @(
    'Todo.React',
    'Todo.Vue',
    'Todo.Svelte',
    'Todo.Angular'
)

Push-Location $repositoryRoot
try {
    foreach ($name in $projects) {
        $project = "samples/$name/$name.csproj"
        if (-not $NoBuild) {
            dotnet build $project `
                --configuration $Configuration `
                -p:WebUIToolkitBuildMode=Development
            if ($LASTEXITCODE -ne 0) {
                throw "$name failed to build."
            }
        }

        # Run serially: every case owns a real Chromium process and profile.
        dotnet run `
            --project $project `
            --configuration $Configuration `
            --no-build `
            -- `
            --browser-smoke-test
        if ($LASTEXITCODE -ne 0) {
            throw "$name Simple browser round trip failed."
        }

        dotnet run `
            --project $project `
            --configuration $Configuration `
            --no-build `
            -- `
            --advanced `
            --browser-smoke-test
        if ($LASTEXITCODE -ne 0) {
            throw "$name Advanced browser round trip failed."
        }
    }
}
finally {
    Pop-Location
}
