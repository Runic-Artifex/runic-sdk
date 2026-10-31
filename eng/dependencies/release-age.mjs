// D038: third-party packages resolve to versions published at least a day ago, so a version published while an
// install runs (sdk#772's queue run met devframe@1.3.0 3 s after its install started) cannot fail it. The Runic
// packages are first-party and exempt; the checks install them from local archives anyway. No dependencies.

/** The minimum age of a third-party version, in seconds. */
export const minimumReleaseAge = 24 * 60 * 60;

/** The first-party npm packages, exempt from the minimum age. */
export const firstPartyScope = "@runic-artifex/*";

/** The cutoff: only versions published at or before this ISO time resolve. */
export const publishedBefore = (now = Date.now()) => new Date(now - minimumReleaseAge * 1000).toISOString();

/**
 * The arguments for an `npm install` that resolves without a lockfile. A pinned third-party version newer than the
 * cutoff fails with ETARGET, naming the date; `file:` archives and the first-party scope are not filtered.
 */
export const npmReleaseAgeArguments = (now = Date.now()) =>
  [`--before=${publishedBefore(now)}`, `--min-release-age-exclude=${firstPartyScope}`];
