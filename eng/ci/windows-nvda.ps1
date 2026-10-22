# Creates a portable NVDA for the accessibility probe (Runic.Desktop.AccessibilityProbe --screen-reader nvda --nvda
# <Destination>\nvda.exe). The launcher is pinned by version and SHA-256 and must carry NV Access's valid Authenticode
# signature; NV Access publishes its SHA-1 (https://api.nvaccess.org/nvdaUpdateCheck?versionType=stable). The probe
# gives NVDA its own configuration, so nothing here changes the runner's settings.
# Usage: windows-nvda.ps1 -Destination <directory>
param([Parameter(Mandatory)] [string] $Destination)
$ErrorActionPreference = 'Stop'
$version = '2026.2'
$sha256 = 'F3F8D29974A88D687B3C4809BE192219EC579C5BDABCDA5AAF53635288BCA824'

$download = Join-Path ([IO.Path]::GetTempPath()) "nvda-$version"
New-Item -ItemType Directory -Force $download | Out-Null
$launcher = Join-Path $download "nvda_$version.exe"
for ($attempt = 1; ; $attempt++) {
  try {
    Invoke-WebRequest "https://download.nvaccess.org/releases/$version/nvda_$version.exe" -OutFile $launcher -TimeoutSec 300
    break
  } catch {
    if ($attempt -ge 3) { throw }
    Write-Warning "Downloading NVDA failed ($_); retrying."
    Start-Sleep -Seconds (10 * $attempt)
  }
}
$actual = (Get-FileHash $launcher -Algorithm SHA256).Hash
if ($actual -ne $sha256) { throw "nvda_$version.exe has SHA-256 $actual, not $sha256." }
$signature = Get-AuthenticodeSignature $launcher
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '^CN=NV Access Limited,') {
  throw "nvda_$version.exe is not signed by NV Access: $($signature.Status) $($signature.SignerCertificate.Subject)"
}

$installer = Start-Process $launcher -ArgumentList '--create-portable-silent', "--portable-path=$Destination" -Wait -PassThru
if ($installer.ExitCode -ne 0) { throw "Creating the portable NVDA failed with exit code $($installer.ExitCode)." }
if (-not (Test-Path (Join-Path $Destination 'nvda.exe'))) { throw "$Destination has no nvda.exe." }
Remove-Item -Recurse -Force $download
"NVDA $version is in $Destination."
