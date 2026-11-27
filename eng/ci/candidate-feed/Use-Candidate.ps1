# Points a project at the runic-sdk release candidate in this folder (eng/ci/candidate-feed.mjs in runic-sdk, D077.24).
#
#   Use-Candidate.ps1 -New <name> [-TemplateArguments '--frontend','react','--package-manager','bun']
#     creates apps\<name> with the candidate's Runic.Application.Templates (installed into this folder's own template
#     hive) and points it at the candidate;
#   Use-Candidate.ps1 -Path <project folder>
#     points an existing project at it.
#
# NuGet: a project below this folder uses its NuGet.config (the candidate folder feed and nuget.org, with the packages
# in .\packages); any other project gets a copy. npm: each frontend (the project folder or a direct subfolder with a
# package.json) gets the lines of candidate.npmrc in its .npmrc (the @runic-artifex scope from the forwarded registry,
# npm's cache in .\npm-cache), bunfig.toml (Bun's cache in .\bun-cache) and, for npm, a package-lock.json bound to the
# registry (bind-npm-lock.mjs). Nothing outside this folder and the project changes. The registry answers while
# `bun eng/ci/candidate-feed.mjs serve` runs on the host.
[CmdletBinding(DefaultParameterSetName = 'Path')]
param(
    [Parameter(Mandatory, ParameterSetName = 'New')][string]$New,
    [Parameter(ParameterSetName = 'New')][string[]]$TemplateArguments = @(),
    [Parameter(Mandatory, ParameterSetName = 'Path')][string]$Path
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$candidate = Get-Content -Raw -LiteralPath (Join-Path $root 'candidate.json') | ConvertFrom-Json
function Assert-Exit([string]$what) { if ($LASTEXITCODE) { throw "$what failed ($LASTEXITCODE)." } }

if ($New) {
    $hive = Join-Path $root 'hive'
    if (!(Test-Path -LiteralPath $hive)) {
        $template = @(Get-ChildItem -LiteralPath (Join-Path $root 'nuget') -Filter 'Runic.Application.Templates.*.nupkg')
        if ($template.Count -ne 1) { throw "Expected one Runic.Application.Templates package in $root\nuget." }
        & dotnet new install $template[0].FullName --debug:custom-hive $hive | Out-Host; Assert-Exit 'dotnet new install'
    }
    $Path = Join-Path $root "apps\$New"
    if (Test-Path -LiteralPath $Path) { throw "$Path exists." }
    & dotnet new runic-app -n $New -o $Path --debug:custom-hive $hive @TemplateArguments | Out-Host; Assert-Exit 'dotnet new runic-app'
}

$Path = (Resolve-Path -LiteralPath $Path).Path
if (-not $Path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    $config = Join-Path $Path 'NuGet.config'
    if (Test-Path -LiteralPath $config) { throw "$config exists: add the runic-candidate source and mapping of $root\NuGet.config to it by hand." }
    Copy-Item -LiteralPath (Join-Path $root 'NuGet.config') $config
    "NuGet: $config"
}

$npmrc = [IO.File]::ReadAllText((Join-Path $root 'candidate.npmrc'))
$bunfig = [IO.File]::ReadAllText((Join-Path $root 'bunfig.toml'))
$frontends = @(@(Get-Item -LiteralPath $Path) + @(Get-ChildItem -LiteralPath $Path -Directory) |
    Where-Object { $_.Name -ne 'node_modules' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'package.json')) })
foreach ($frontend in $frontends.FullName) {
    $file = Join-Path $frontend '.npmrc'
    $text = if (Test-Path -LiteralPath $file) { [IO.File]::ReadAllText($file) } else { '' }
    if (-not $text.Contains($npmrc.Split("`n")[0])) {
        if ($text -and -not $text.EndsWith("`n")) { $text += "`n" }
        [IO.File]::WriteAllText($file, $text + $npmrc)
    }
    # An existing bunfig.toml must be this folder's (an earlier Use-Candidate.ps1 or a committed copy): anything else
    # would lose its settings. Git's core.autocrlf checks a committed copy out with CRLF, so line endings don't count.
    $file = Join-Path $frontend 'bunfig.toml'
    if (Test-Path -LiteralPath $file) {
        if ([IO.File]::ReadAllText($file).Replace("`r`n", "`n") -cne $bunfig) { throw "$file exists: add the [install.cache] of $root\bunfig.toml to it by hand." }
    } else { [IO.File]::WriteAllText($file, $bunfig) }
    $lock = Join-Path $frontend 'package-lock.json'
    if (Test-Path -LiteralPath $lock) { & node (Join-Path $root 'bind-npm-lock.mjs') $lock | Out-Host; Assert-Exit 'bind-npm-lock.mjs' }
    "npm: $frontend"
}
"$Path uses runic-sdk $($candidate.sdk.version) from run $($candidate.sdk.run.id) ($($candidate.sdk.run.headSha)); npm registry http://127.0.0.1:$($candidate.machine.port)/"
