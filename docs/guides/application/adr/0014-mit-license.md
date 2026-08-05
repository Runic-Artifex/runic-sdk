# ADR 0014: MIT is the repository license

- Status: Accepted
- Date: 2026-08-04
- Supersedes the monorepo publication hold recorded in Git history.

## Context

Runic Toolkit and the independently evolving RunicArtifex projects are intended
to be reusable across applications, UI frameworks, hosts, and languages. The
previous publication hold deferred selection of a license while ownership,
dependency, notice, and package-boundary work progressed.

## Decision

Repository-owned source, documentation, specifications, fixtures, examples, and
package artifacts are licensed under the MIT License recorded in the root
`LICENSE` file.

The MIT license does not replace or modify third-party terms. Existing notices,
vendored license files, attributions, and redistribution requirements remain in
force for their respective components. A component that cannot be distributed
under the repository license must retain an explicit exception or be excluded
from publication.

NuGet packages use the SPDX expression `MIT`. npm package metadata uses
`"license": "MIT"`, including private workspaces where the field documents the
intended license independently of publication state.

## Consequences

The license-specific publication hold from ADR 0004 is lifted. Creating public
repositories and packages still requires package identity ownership, dependency
and notice verification, security and quality gates, and an explicit release
decision. Those operational gates do not change the source license.
