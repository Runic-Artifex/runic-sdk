// NuGet versions and version ranges, as far as the train gate needs them (SemVer 2.0 without build metadata).

const VERSION = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/;

export function parseVersion(text) {
  const match = VERSION.exec(String(text).trim());
  if (!match) throw new Error(`Not a NuGet version: ${text}`);
  const [, major, minor, patch, revision, prerelease] = match;
  return { major: Number(major), minor: Number(minor), patch: Number(patch), revision: Number(revision ?? 0),
    prerelease: prerelease ? prerelease.split('.') : [], text: String(text).trim() };
}

const compareIdentifiers = (a, b) => {
  const numeric = /^\d+$/;
  if (numeric.test(a) && numeric.test(b)) return Math.sign(Number(a) - Number(b));
  if (numeric.test(a)) return -1;
  if (numeric.test(b)) return 1;
  const [x, y] = [a.toLowerCase(), b.toLowerCase()];
  return x < y ? -1 : x > y ? 1 : 0;
};

export function compareVersions(left, right) {
  const [a, b] = [typeof left === 'string' ? parseVersion(left) : left, typeof right === 'string' ? parseVersion(right) : right];
  for (const key of ['major', 'minor', 'patch', 'revision']) if (a[key] !== b[key]) return Math.sign(a[key] - b[key]);
  if (!a.prerelease.length || !b.prerelease.length) return Math.sign(b.prerelease.length - a.prerelease.length);
  for (let index = 0; index < Math.min(a.prerelease.length, b.prerelease.length); index++) {
    const order = compareIdentifiers(a.prerelease[index], b.prerelease[index]);
    if (order) return order;
  }
  return Math.sign(a.prerelease.length - b.prerelease.length);
}

// The release a version belongs to: 0.17.1-preview.1 belongs to 0.17.1.
export const releaseOf = version => { const v = parseVersion(version); return `${v.major}.${v.minor}.${v.patch}`; };

// The exclusive upper bound of a version's compatibility line (D061.13): the next minor before 1.0, the next major
// from 1.0, always in the `-0.0` form so that the next line's prereleases are excluded too.
export function lineUpperBound(version) {
  const v = parseVersion(version);
  return v.major === 0 ? `0.${v.minor + 1}.0-0.0` : `${v.major + 1}.0.0-0.0`;
}

// The same compatibility line: the same minor before 1.0, the same major from 1.0.
export function sameLine(a, b) {
  const [x, y] = [parseVersion(a), parseVersion(b)];
  return x.major === y.major && (x.major !== 0 || x.minor === y.minor);
}

// Parses a nuspec dependency version: "1.0" (>= 1.0), "[1.0]", "[1.0, 2.0)", "(, 2.0]" and the like.
export function parseRange(text) {
  const raw = String(text ?? '').trim();
  if (!raw) return { raw, min: null, minInclusive: false, max: null, maxInclusive: false };
  if (!/^[[(]/.test(raw)) return { raw, min: raw, minInclusive: true, max: null, maxInclusive: false };
  const match = /^([[(])\s*([^,\s]*)\s*(?:,\s*([^\s\])]*)\s*)?([\])])$/.exec(raw);
  if (!match) throw new Error(`Not a NuGet version range: ${raw}`);
  const [, open, min, max, close] = match;
  if (max === undefined) {
    if (open !== '[' || close !== ']') throw new Error(`Not a NuGet version range: ${raw}`);
    return { raw, min, minInclusive: true, max: min, maxInclusive: true };
  }
  for (const bound of [min, max]) if (bound) parseVersion(bound);
  return { raw, min: min || null, minInclusive: open === '[', max: max || null, maxInclusive: close === ']' };
}

export function rangeContains(range, version) {
  const r = typeof range === 'string' ? parseRange(range) : range;
  if (r.min) { const order = compareVersions(version, r.min); if (order < 0 || (order === 0 && !r.minInclusive)) return false; }
  if (r.max) { const order = compareVersions(version, r.max); if (order > 0 || (order === 0 && !r.maxInclusive)) return false; }
  return true;
}
