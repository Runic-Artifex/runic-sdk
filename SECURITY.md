# Security policy

## Reporting a vulnerability

Report vulnerabilities privately through
[GitHub private vulnerability reporting](https://github.com/Runic-Artifex/runic-sdk/security/advisories/new)
(**Security** > **Report a vulnerability**). Do not open a public issue, pull
request or discussion for an unfixed vulnerability.

Include the affected package and version, the operating system and host
(Runic Desktop, CsWebUi or browser), reproduction steps, and the impact you
observed. We acknowledge reports as soon as we can, keep you informed while we
investigate, and credit you in the advisory unless you prefer otherwise.

## Supported versions

Until 1.0, security fixes ship as a patch release of the latest minor release
(for example 0.9.1 after 0.9.0); earlier minors are not patched. Upgrade to the
[latest release](https://github.com/Runic-Artifex/runic-sdk/releases) before
reporting. From 1.0, the
[support policy](eng/release/README.md#fixes-and-support) in the release guide
applies.

## Building secure applications

The [security guide](docs/security.md) explains what Runic protects by default,
which options relax it, and what an application must check itself: Bridge input,
embedded windows and installed browsers, surface admission, framing and
Content-Security-Policy, permissions, CS-WebUI windows and packed assets.

## Scope

This repository covers the `Runic.*` NuGet packages, the `@runic-artifex/*` npm
packages, the `dotnet runic` tool and the project templates published from it.
Report issues in Runic CommandLine, Runic Translations or the website to their
own repositories:
[runic-cli-sdk](https://github.com/Runic-Artifex/runic-cli-sdk/security),
[runic-translations-sdk](https://github.com/Runic-Artifex/runic-translations-sdk/security) and
[runic-site](https://github.com/Runic-Artifex/runic-site/security).

## Verifying releases

Every published NuGet package, npm package and GitHub release asset has a signed
build-provenance attestation, and each release has a CycloneDX SBOM that is
attached to the release and attested for its packages. Verify a file with
`gh attestation verify <file> -R Runic-Artifex/runic-sdk --signer-workflow Runic-Artifex/runic-sdk/.github/workflows/publish-preview.yml`.
The attestation shows that the publish workflow of this repository, which runs
only on `main`, published those bytes. It does not build them: it takes the
packages from a successful CI run of the same commit and checks them by hash
before attesting them. Packages downloaded
from NuGet.org carry NuGet.org's repository signature and therefore differ from
the attested bytes; verify the copy from the release's package bundle instead. See
[verifying a release](eng/release/README.md#verifying-a-release).

## Dependency audits

Dependencies are audited weekly by the
[dependency audit workflow](.github/workflows/dependency-audit.yml).
