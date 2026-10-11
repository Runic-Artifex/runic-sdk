import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { gunzipSync, gzipSync } from 'node:zlib';
import { REPOSITORY } from './artifacts.mjs';
import { assertReleaseLinks, releaseLinks } from './readme-links.mjs';

const npmArchiveName = (name, version) => `${name.replace('@', '').replace('/', '-')}-${version}.tgz`;
const root = resolve(import.meta.dirname, '../..');

// Bun resolves workspace: ranges from the checkout, so pack in place, then stamp
// gitHead into the archive and point the README and homepage links at the
// release tag. The source files are never written.
export function packNpm(directory, destination, source) {
  assert.match(source, /^[a-f0-9]{40}$/);
  const manifest = JSON.parse(readFileSync(join(directory, 'package.json'), 'utf8'));
  const archive = join(destination, npmArchiveName(manifest.name, manifest.version));
  // Bun's summary would report the integrity of the unstamped archive.
  execFileSync('bun', ['pm', 'pack', '--quiet', '--destination', destination], { cwd: directory, stdio: ['ignore', 'ignore', 'inherit'] });
  const links = { repository: REPOSITORY, tag: `v${manifest.version}`, directory: relative(root, directory) };
  stampArchive(archive, {
    'package/package.json': text => {
      const packed = JSON.parse(text);
      const homepage = packed.homepage === undefined ? {} : { homepage: releaseLinks(packed.homepage, links) };
      return JSON.stringify({ ...packed, ...homepage, gitHead: source }, null, 2) + '\n';
    },
    'package/README.md': text => {
      const stamped = releaseLinks(text, links);
      assertReleaseLinks(stamped, `${manifest.name} README.md`);
      return stamped;
    },
  });
  console.log(`Packed ${archive}`);
  return archive;
}

const text = (header, start, length) => {
  const bytes = header.subarray(start, start + length);
  const end = bytes.indexOf(0);
  return bytes.subarray(0, end < 0 ? length : end).toString('utf8');
};

// Rewrites the named files of a gzipped ustar archive and copies every other
// entry byte-for-byte. package/package.json must exist; other files are
// rewritten when present.
function stampArchive(archive, rewrites) {
  const tar = gunzipSync(readFileSync(archive));
  const output = [];
  const stamped = new Map();
  let offset = 0, extended = false;
  while (offset + 512 <= tar.length) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(byte => byte === 0)) break;
    const prefix = text(header, 345, 155), name = text(header, 0, 100);
    const path = prefix ? `${prefix}/${name}` : name;
    const size = parseInt(text(header, 124, 12).trim() || '0', 8);
    const type = String.fromCharCode(header[156] || 48);
    const end = offset + 512 + Math.ceil(size / 512) * 512;
    if (type === '0' && Object.hasOwn(rewrites, path)) {
      assert(!extended, `${path} must not use an extended tar header`);
      const bytes = Buffer.from(rewrites[path](tar.subarray(offset + 512, offset + 512 + size).toString('utf8')));
      const updated = Buffer.from(header);
      updated.write(bytes.length.toString(8).padStart(11, '0') + '\0', 124, 12, 'latin1');
      updated.fill(0x20, 148, 156);
      const checksum = updated.reduce((sum, byte) => sum + byte, 0);
      updated.write(checksum.toString(8).padStart(6, '0') + '\0 ', 148, 8, 'latin1');
      output.push(updated, bytes, Buffer.alloc((512 - bytes.length % 512) % 512));
      stamped.set(path, (stamped.get(path) ?? 0) + 1);
    } else {
      output.push(tar.subarray(offset, end));
    }
    extended = type === 'x';
    offset = end;
  }
  assert.equal(stamped.get('package/package.json'), 1, `${archive} must contain exactly one package/package.json`);
  for (const [path, count] of stamped) assert.equal(count, 1, `${archive} must contain ${path} once`);
  output.push(Buffer.alloc(1024));
  writeFileSync(archive, gzipSync(Buffer.concat(output), { level: 9 }));
}
