import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { gunzipSync, gzipSync } from 'node:zlib';

const npmArchiveName = (name, version) => `${name.replace('@', '').replace('/', '-')}-${version}.tgz`;

// Bun resolves workspace: ranges from the checkout, so pack in place, then stamp
// gitHead into the archive. The source manifest is never written.
export function packNpm(directory, destination, source) {
  assert.match(source, /^[a-f0-9]{40}$/);
  const manifest = JSON.parse(readFileSync(join(directory, 'package.json'), 'utf8'));
  const archive = join(destination, npmArchiveName(manifest.name, manifest.version));
  // Bun's summary would report the integrity of the unstamped archive.
  execFileSync('bun', ['pm', 'pack', '--quiet', '--destination', destination], { cwd: directory, stdio: ['ignore', 'ignore', 'inherit'] });
  stampGitHead(archive, source);
  console.log(`Packed ${archive}`);
  return archive;
}

const text = (header, start, length) => {
  const bytes = header.subarray(start, start + length);
  const end = bytes.indexOf(0);
  return bytes.subarray(0, end < 0 ? length : end).toString('utf8');
};

// Rewrites package/package.json in a gzipped ustar archive and copies every
// other entry byte-for-byte.
export function stampGitHead(archive, source) {
  assert.match(source, /^[a-f0-9]{40}$/);
  const tar = gunzipSync(readFileSync(archive));
  const output = [];
  let offset = 0, stamped = 0, extended = false;
  while (offset + 512 <= tar.length) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(byte => byte === 0)) break;
    const prefix = text(header, 345, 155), name = text(header, 0, 100);
    const path = prefix ? `${prefix}/${name}` : name;
    const size = parseInt(text(header, 124, 12).trim() || '0', 8);
    const type = String.fromCharCode(header[156] || 48);
    const end = offset + 512 + Math.ceil(size / 512) * 512;
    if (type === '0' && path === 'package/package.json') {
      assert(!extended, 'package.json must not use an extended tar header');
      const manifest = JSON.parse(tar.subarray(offset + 512, offset + 512 + size).toString('utf8'));
      const bytes = Buffer.from(JSON.stringify({ ...manifest, gitHead: source }, null, 2) + '\n');
      const updated = Buffer.from(header);
      updated.write(bytes.length.toString(8).padStart(11, '0') + '\0', 124, 12, 'latin1');
      updated.fill(0x20, 148, 156);
      const checksum = updated.reduce((sum, byte) => sum + byte, 0);
      updated.write(checksum.toString(8).padStart(6, '0') + '\0 ', 148, 8, 'latin1');
      output.push(updated, bytes, Buffer.alloc((512 - bytes.length % 512) % 512));
      stamped++;
    } else {
      output.push(tar.subarray(offset, end));
    }
    extended = type === 'x';
    offset = end;
  }
  assert.equal(stamped, 1, `${archive} must contain exactly one package/package.json`);
  output.push(Buffer.alloc(1024));
  writeFileSync(archive, gzipSync(Buffer.concat(output), { level: 9 }));
}
