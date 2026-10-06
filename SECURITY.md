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

Runic SDK is in preview. Fixes ship in the next preview release; earlier
previews do not receive backported fixes. Upgrade to the
[latest release](https://github.com/Runic-Artifex/runic-sdk/releases) before
reporting.

## Scope

This repository covers the `Runic.*` NuGet packages, the `@runic-artifex/*` npm
packages, the `dotnet runic` tool and the project templates published from it.
Report issues in Runic CommandLine, Runic Translations or the website to their
own repositories:
[runic-cli-sdk](https://github.com/Runic-Artifex/runic-cli-sdk/security),
[runic-translations-sdk](https://github.com/Runic-Artifex/runic-translations-sdk/security) and
[runic-site](https://github.com/Runic-Artifex/runic-site/security).

Dependencies are audited weekly by the
[dependency audit workflow](.github/workflows/dependency-audit.yml).
