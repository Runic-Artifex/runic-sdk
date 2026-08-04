[CmdletBinding()]
param(
    [string[]] $Candidate = @('SystemCommandLine', 'SpectreConsoleCli', 'Cocona', 'CommandLineParser', 'InHouse'),
    [string] $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,
    [string] $OutputDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'runiccommandline-cli-evaluation-results'),
    [switch] $KeepWorkspace
)

$ErrorActionPreference = 'Stop'
$evaluationRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw -LiteralPath (Join-Path $evaluationRoot 'candidate-manifest.json') | ConvertFrom-Json
$corpus = Get-Content -Raw -LiteralPath (Join-Path $evaluationRoot 'corpus/grammar.json') | ConvertFrom-Json
$Candidate = @($Candidate | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ('runiccommandline-cli-evaluation-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $workspace)
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)

function Invoke-CapturedProcess {
    param([string] $FileName, [string[]] $ArgumentList, [string] $WorkingDirectory, [hashtable] $Environment = @{})
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) { [void]$startInfo.ArgumentList.Add($argument) }
    foreach ($entry in $Environment.GetEnumerator()) { $startInfo.Environment[$entry.Key] = $entry.Value }
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [System.Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask))
    $stopwatch.Stop()
    return [ordered]@{
        exitCode = $process.ExitCode
        elapsedMilliseconds = $stopwatch.ElapsedMilliseconds
        stdout = $stdoutTask.Result
        stderr = $stderrTask.Result
    }
}

try {
    $actualSdk = (& dotnet --version).Trim()
    if ($actualSdk -ne $manifest.sdkVersion) {
        throw "Evaluation requires SDK $($manifest.sdkVersion); current SDK is $actualSdk."
    }

    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($candidateId in $Candidate) {
        $candidateMetadata = $manifest.candidates | Where-Object id -eq $candidateId
        if ($null -eq $candidateMetadata) { throw "Unknown candidate '$candidateId'." }

        $source = Join-Path $evaluationRoot ("fixtures/" + $candidateId)
        $candidateRoot = Join-Path $workspace $candidateId
        [void](New-Item -ItemType Directory -Path $candidateRoot)
        Copy-Item -LiteralPath (Join-Path $source 'Program.cs') -Destination $candidateRoot
        Copy-Item -LiteralPath (Join-Path $evaluationRoot 'fixtures/Common/FixtureOutput.cs') -Destination $candidateRoot
        Copy-Item -LiteralPath (Join-Path $source 'Fixture.csproj.in') -Destination (Join-Path $candidateRoot 'Fixture.csproj')
        [System.IO.File]::WriteAllText((Join-Path $candidateRoot 'global.json'), "{`n  `"sdk`": { `"version`": `"$($manifest.sdkVersion)`", `"rollForward`": `"disable`" }`n}`n")
        [System.IO.File]::WriteAllText((Join-Path $candidateRoot 'NuGet.config'), "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<configuration>`n  <packageSources>`n    <clear />`n    <add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`" protocolVersion=`"3`" />`n  </packageSources>`n</configuration>`n")

        $restore = Invoke-CapturedProcess 'dotnet' @('restore', 'Fixture.csproj', '--nologo') $candidateRoot
        $build = if ($restore.exitCode -eq 0) {
            Invoke-CapturedProcess 'dotnet' @('build', 'Fixture.csproj', '--configuration', 'Release', '--no-restore', '--nologo') $candidateRoot
        } else { [ordered]@{ exitCode = -1; elapsedMilliseconds = 0; stdout = ''; stderr = 'Skipped: restore failed.' } }

        $publish = if ($build.exitCode -eq 0) {
            Invoke-CapturedProcess 'dotnet' @('publish', 'Fixture.csproj', '--configuration', 'Release', '--runtime', $RuntimeIdentifier, '--self-contained', 'true', '--nologo') $candidateRoot
        } else { [ordered]@{ exitCode = -1; elapsedMilliseconds = 0; stdout = ''; stderr = 'Skipped: build failed.' } }

        $caseResults = [System.Collections.Generic.List[object]]::new()
        $dll = Join-Path $candidateRoot 'bin/Release/net10.0/Fixture.dll'
        if ($build.exitCode -eq 0 -and (Test-Path -LiteralPath $dll)) {
            foreach ($case in $corpus.cases) {
                $caseArgs = [System.Collections.Generic.List[string]]::new()
                $caseArgs.Add($dll)
                foreach ($token in $case.args) { $caseArgs.Add([string]$token) }
                $environment = @{}
                if ($null -ne $case.culture) { $environment['WUT_FIXTURE_CULTURE'] = [string]$case.culture }
                $run = Invoke-CapturedProcess 'dotnet' $caseArgs.ToArray() $candidateRoot $environment
                $actualDisposition = 'error'
                $actualPath = $null
                if ($run.exitCode -eq 0) {
                    try {
                        $payload = $run.stdout.Trim() | ConvertFrom-Json
                        if ($payload.disposition -eq 'invoke') {
                            $actualDisposition = 'invoke'
                            $actualPath = $payload.commandPath
                        } elseif ($case.expected.disposition -eq 'version') { $actualDisposition = 'version' }
                        else { $actualDisposition = 'help' }
                    } catch {
                        $actualDisposition = if ($case.expected.disposition -eq 'version') { 'version' } else { 'help' }
                    }
                }
                $matches = $actualDisposition -eq $case.expected.disposition
                if ($matches -and $actualDisposition -eq 'invoke') { $matches = $actualPath -eq $case.expected.commandPath }
                $caseResults.Add([ordered]@{
                    id = $case.id
                    matchesPortableDisposition = $matches
                    expectedDisposition = $case.expected.disposition
                    actualDisposition = $actualDisposition
                    expectedCommandPath = $case.expected.commandPath
                    actualCommandPath = $actualPath
                    exitCode = $run.exitCode
                    elapsedMilliseconds = $run.elapsedMilliseconds
                    stdout = $run.stdout
                    stderr = $run.stderr
                    bridge = $case.expected.bridge
                })
            }
        }

        $publishDirectory = Join-Path $candidateRoot ("bin/Release/net10.0/" + $RuntimeIdentifier + '/publish')
        $publishedBytes = if (Test-Path -LiteralPath $publishDirectory) {
            (Get-ChildItem -File -Recurse -LiteralPath $publishDirectory | Measure-Object -Property Length -Sum).Sum
        } else { 0 }
        $analysisText = $build.stdout + "`n" + $build.stderr + "`n" + $publish.stdout + "`n" + $publish.stderr
        $results.Add([ordered]@{
            candidate = $candidateId
            package = $candidateMetadata.package
            version = $candidateMetadata.version
            restore = $restore
            build = $build
            nativeAotPublish = $publish
            aotOrTrimWarningDetected = [bool]($analysisText -match 'IL(2026|3050|2104|3053)')
            publishedBytes = $publishedBytes
            corpus = [ordered]@{
                executed = $caseResults.Count
                matched = @($caseResults | Where-Object matchesPortableDisposition).Count
                total = $corpus.cases.Count
                cases = $caseResults
            }
        })
    }

    $document = [ordered]@{
        schemaVersion = 'runic.commandline.evaluation-results/1'
        evaluatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sdkVersion = $actualSdk
        runtimeIdentifier = $RuntimeIdentifier
        protocolIdentity = 'runic.commandline/1'
        outputEnvironmentVariable = 'RUNIC_COMMANDLINE_OUTPUT'
        temporaryWorkspace = if ($KeepWorkspace) { $workspace } else { '<deleted>' }
        candidates = $results
    }
    $resultPath = Join-Path $OutputDirectory ("evaluation-$RuntimeIdentifier.json")
    [System.IO.File]::WriteAllText($resultPath, ($document | ConvertTo-Json -Depth 12) + "`n")
    Write-Output $resultPath
} finally {
    if (-not $KeepWorkspace -and (Test-Path -LiteralPath $workspace)) {
        Remove-Item -LiteralPath $workspace -Recurse -Force
    }
}
