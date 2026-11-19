// Writes small npm (gzipped ustar) and NuGet (zip) archives for the release tests, without external tools.
import { writeFileSync } from 'node:fs';
import { crc32, gzipSync } from 'node:zlib';

// `files` is a list of [name, text or bytes].
export function writeTarGz(path, files) {
  const blocks = [];
  for (const [name, content] of files) {
    const data = Buffer.from(content);
    const header = Buffer.alloc(512);
    if (Buffer.byteLength(name) > 100) throw new Error(`${name} needs a long-name tar header`);
    header.write(name, 0, 100, 'utf8');
    header.write('0000644\0', 100, 'latin1');
    header.write('0000000\0', 108, 'latin1');
    header.write('0000000\0', 116, 'latin1');
    header.write(data.length.toString(8).padStart(11, '0') + '\0', 124, 'latin1');
    header.write('00000000000\0', 136, 'latin1');
    header.fill(0x20, 148, 156);
    header.write('0', 156, 'latin1');
    header.write('ustar\0' + '00', 257, 'latin1');
    header.write(header.reduce((sum, byte) => sum + byte, 0).toString(8).padStart(6, '0') + '\0 ', 148, 'latin1');
    blocks.push(header, data, Buffer.alloc((512 - data.length % 512) % 512));
  }
  blocks.push(Buffer.alloc(1024));
  writeFileSync(path, gzipSync(Buffer.concat(blocks)));
}

// `files` is a list of [name, text or bytes]; entries are stored uncompressed.
export function writeZip(path, files) {
  const local = [], central = [];
  let offset = 0;
  for (const [name, content] of files) {
    const data = Buffer.from(content), nameBytes = Buffer.from(name, 'utf8'), crc = crc32(data);
    const header = Buffer.alloc(30);
    header.writeUInt32LE(0x04034b50, 0); header.writeUInt16LE(20, 4); header.writeUInt16LE(0x0800, 6);
    header.writeUInt32LE(crc, 14); header.writeUInt32LE(data.length, 18); header.writeUInt32LE(data.length, 22);
    header.writeUInt16LE(nameBytes.length, 26);
    const entry = Buffer.alloc(46);
    entry.writeUInt32LE(0x02014b50, 0); entry.writeUInt16LE(20, 4); entry.writeUInt16LE(20, 6); entry.writeUInt16LE(0x0800, 8);
    entry.writeUInt32LE(crc, 16); entry.writeUInt32LE(data.length, 20); entry.writeUInt32LE(data.length, 24);
    entry.writeUInt16LE(nameBytes.length, 28); entry.writeUInt32LE(offset, 42);
    local.push(header, nameBytes, data);
    central.push(entry, nameBytes);
    offset += header.length + nameBytes.length + data.length;
  }
  const directory = Buffer.concat(central), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(files.length, 8); end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  writeFileSync(path, Buffer.concat([...local, directory, end]));
}
