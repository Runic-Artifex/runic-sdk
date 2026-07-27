[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
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

Push-Location $repositoryRoot
try {
    dotnet run `
        --project tools/dotnet-webuitoolkit/WebUIToolkit.DotNet.WebUIToolkit.csproj `
        --configuration $Configuration `
        --no-build `
        -- `
        dev samples/SimpleTodo/SimpleTodo.csproj `
        --configuration $Configuration `
        --no-restore `
        -- `
        --browser-smoke-test
    if ($LASTEXITCODE -ne 0) {
        throw 'The native-window Vite HMR gate failed.'
    }
}
finally {
    Pop-Location
}
