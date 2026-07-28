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

$projects = @('Todo.React', 'Todo.Vue', 'Todo.Svelte', 'Todo.Angular')
$previousHmrSource = $env:WEBUITOOLKIT_TODO_HMR_SOURCE
$env:WEBUITOOLKIT_TODO_HMR_SOURCE = Join-Path `
    $repositoryRoot `
    'samples/Todo.Frontends/shared/styles.css'

Push-Location $repositoryRoot
try {
    if (-not $NoBuild) {
        dotnet build `
            tools/dotnet-webuitoolkit/WebUIToolkit.DotNet.WebUIToolkit.csproj `
            --configuration $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet webuitoolkit failed to build.'
        }
    }

    foreach ($name in $projects) {
        $project = "samples/$name/$name.csproj"
        dotnet run `
            --project tools/dotnet-webuitoolkit/WebUIToolkit.DotNet.WebUIToolkit.csproj `
            --configuration $Configuration `
            --no-build `
            -- `
            dev $project `
            --configuration $Configuration `
            --no-restore `
            --no-dotnet-watch `
            -- `
            --browser-smoke-test `
            --hmr-smoke-test
        if ($LASTEXITCODE -ne 0) {
            throw "$name failed its native retained-state HMR gate."
        }
    }

    Write-Host (
        'PASS: React Fast Refresh, Vue SFC HMR, Svelte HMR, and Angular ' +
        'development-server HMR retained the native window and C# Todo state.')
}
finally {
    Pop-Location
    $env:WEBUITOOLKIT_TODO_HMR_SOURCE = $previousHmrSource
}
