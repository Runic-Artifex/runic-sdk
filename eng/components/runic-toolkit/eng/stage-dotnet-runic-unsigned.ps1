param(
    [Parameter(Mandatory = $true)]
    [string]$CommandLineFeed,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [string]$PackageVersion
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "tools/dotnet-runic-toolkit/Runic.Application.Tool.csproj"
$feed = (Resolve-Path $CommandLineFeed).Path
$revision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$tree = (& git -C $repositoryRoot rev-parse 'HEAD^{tree}').Trim()
if (Test-Path $OutputDirectory) { throw "Unsigned tool staging output must not already exist." }
if ($PackageVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$') { throw "Unsigned tool staging requires a SemVer-compatible package version." }
if ((& git -C $repositoryRoot status --porcelain)) { throw "Unsigned tool staging requires a clean source worktree." }

[xml]$projectXml = Get-Content -Raw $project
if (@($projectXml.SelectNodes("//*[local-name()='ProjectReference']")).Count -ne 0) { throw "The direct tool staging project must not contain source ProjectReferences." }

function Get-Digest([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }
function Get-EntryDigest([System.IO.Compression.ZipArchiveEntry]$Entry) {
    $buffer = [IO.MemoryStream]::new()
    $stream = $Entry.Open()
    try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($buffer.ToArray())).ToLowerInvariant()
}
function Get-PackageMetadata([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $nuspecEntry = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)[^/]+\.nuspec$' })
        $settingsEntry = @($archive.Entries | Where-Object { $_.FullName -eq 'tools/net10.0/any/DotnetToolSettings.xml' })
        if ($nuspecEntry.Count -ne 1 -or $settingsEntry.Count -ne 1) { throw "The direct tool package has incomplete NuGet tool metadata." }
        $reader = [IO.StreamReader]::new($nuspecEntry[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $settingsReader = [IO.StreamReader]::new($settingsEntry[0].Open())
        try { [xml]$settings = $settingsReader.ReadToEnd() } finally { $settingsReader.Dispose() }
        $repository = $nuspec.package.metadata.repository
        $dependencyProperty = $nuspec.package.metadata.PSObject.Properties['dependencies']
        $dependencies = if ($null -eq $dependencyProperty) { @() } else { @($dependencyProperty.Value.group.dependency | ForEach-Object { [ordered]@{ id = [string]$_.id; version = [string]$_.version } } | Sort-Object id) }
        return [pscustomobject][ordered]@{
            id = [string]$nuspec.package.metadata.id
            version = [string]$nuspec.package.metadata.version
            nuspecSha256 = (Get-EntryDigest $nuspecEntry[0])
            repository = [pscustomobject][ordered]@{ type = [string]$repository.type; url = [string]$repository.url; commit = [string]$repository.commit }
            toolCommandName = [string]$settings.DotNetCliTool.Commands.Command.Name
            dependencies = @($dependencies)
        }
    }
    finally { $archive.Dispose() }
}

$feedPackages = @(Get-ChildItem -LiteralPath $feed -File -Filter '*.nupkg' | Sort-Object Name)
if ($feedPackages.Count -eq 0 -or @($feedPackages | Where-Object { $_.LinkType }).Count -ne 0) { throw "Unsigned tool staging requires regular local NuGet prerequisite packages." }
$feedMetadata = @($feedPackages | ForEach-Object { [ordered]@{ archive = $_.Name; sha256 = Get-Digest $_.FullName } })
if (@($feedMetadata | Where-Object { $_.archive -eq 'dotnet-runic.' + $PackageVersion + '.nupkg' }).Count -ne 0) { throw "The prerequisite feed must not substitute a prebuilt dotnet-runic package." }

$work = Join-Path ([IO.Path]::GetTempPath()) ("runic-unsigned-tool-stage-" + [Guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Force -Path $work, $OutputDirectory | Out-Null
    $config = Join-Path $work "NuGet.config"
    $artifacts = Join-Path $work "artifacts"
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="local-prerequisites" value="$feed" /></packageSources>
  <packageSourceMapping><packageSource key="local-prerequisites"><package pattern="Runic.CommandLine" /><package pattern="Microsoft.NET.ILLink.Tasks" /></packageSource></packageSourceMapping>
  <config><add key="globalPackagesFolder" value="$(Join-Path $work 'packages')" /></config>
</configuration>
"@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM
    $stageEnv = @{ DOTNET_CLI_HOME = (Join-Path $work ".dotnet"); NUGET_PACKAGES = (Join-Path $work "packages"); NUGET_HTTP_CACHE_PATH = (Join-Path $work "http") }
    foreach ($entry in $stageEnv.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process") }
    & dotnet restore $project --configfile $config --artifacts-path $artifacts --no-cache --force-evaluate --nologo
    if ($LASTEXITCODE -ne 0) { throw "Direct tool restore from the local prerequisite feed failed." }
    & dotnet pack $project --no-restore --configuration Release --artifacts-path $artifacts --output $OutputDirectory --nologo -p:PackageVersion=$PackageVersion -p:Version=$PackageVersion -p:RepositoryCommit=$revision
    if ($LASTEXITCODE -ne 0) { throw "Direct dotnet-runic package creation failed." }
    $archive = Join-Path $OutputDirectory "dotnet-runic.$PackageVersion.nupkg"
    if (-not (Test-Path $archive -PathType Leaf)) { throw "Direct tool staging did not produce dotnet-runic.$PackageVersion.nupkg." }
    $metadata = Get-PackageMetadata $archive
    if ($metadata.id -ne 'dotnet-runic' -or $metadata.version -ne $PackageVersion -or $metadata.toolCommandName -ne 'dotnet-runic' -or
        $metadata.repository.url -ne 'https://github.com/Runic-Artifex/runic-toolkit' -or $metadata.repository.commit -ne $revision -or
        $metadata.dependencies.Count -ne 0) {
        throw "Direct tool NuGet identity or repository provenance drifted."
    }
    $record = [ordered]@{
        schema = 'runic.dotnet-runic-unsigned-staging/1'
        publication = 'forbidden'
        canonicalReleaseApproval = 'seven-package-release-gate-required'
        producer = [ordered]@{ operation = 'direct-dotnet-pack'; script = 'eng/stage-dotnet-runic-unsigned.ps1'; scriptSha256 = Get-Digest $PSCommandPath; project = 'tools/dotnet-runic-toolkit/Runic.Application.Tool.csproj'; fullPackInvoked = $false; sourceProjectReferences = @() }
        source = [ordered]@{ repository = 'https://github.com/Runic-Artifex/runic-toolkit'; revision = $revision; tree = $tree }
        prerequisiteFeed = [ordered]@{ packages = $feedMetadata; remoteSources = @() }
        package = [ordered]@{ archive = [IO.Path]::GetFileName($archive); sha256 = Get-Digest $archive; metadata = $metadata }
        supportEnvelopeContent = 'forbidden'
    }
    $record | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'dotnet-runic-unsigned-staging.json') -Encoding utf8NoBOM
    Write-Host "Staged direct unsigned dotnet-runic package: $archive"
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}
