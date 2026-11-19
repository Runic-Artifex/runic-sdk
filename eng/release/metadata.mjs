// Reads release archive metadata without extracting paths or running package scripts, and without external tools.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { zipEntries } from '../nupkg.mjs';

const utf8 = new TextDecoder('utf-8', { fatal: true });

// The entries of a gzipped tar archive (ustar, pax and GNU long names), as {name, type, data}.
export function tarEntries(path) {
  const tar = gunzipSync(readFileSync(path));
  const field = (header, start, length) => {
    const bytes = header.subarray(start, start + length), end = bytes.indexOf(0);
    return bytes.subarray(0, end < 0 ? length : end).toString('utf8');
  };
  const entries = [];
  let offset = 0, longName = null;
  while (offset + 512 <= tar.length) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(byte => byte === 0)) break;
    const size = parseInt(field(header, 124, 12).trim() || '0', 8);
    if (!Number.isSafeInteger(size)) throw new Error(`${path} has a corrupt tar header`);
    const data = tar.subarray(offset + 512, offset + 512 + size);
    if (data.length !== size) throw new Error(`${path} is truncated`);
    offset += 512 + Math.ceil(size / 512) * 512;
    const type = String.fromCharCode(header[156] || 48);
    if (type === 'x') {
      // A pax header's path record names the next entry.
      for (const record of data.toString('utf8').split('\n')) {
        const match = /^\d+ path=(.*)$/s.exec(record);
        if (match) longName = match[1];
      }
      continue;
    }
    if (type === 'L') { longName = field(data, 0, data.length); continue; }
    if (type === 'g') continue;
    const prefix = field(header, 345, 155), short = field(header, 0, 100);
    let name = longName ?? (prefix ? `${prefix}/${short}` : short);
    longName = null;
    if (type === '5') name = name.replace(/\/+$/, '');
    entries.push({ name, type, data });
  }
  return entries;
}

// A minimal XML element tree: {name (local, namespace removed), attributes, text, children}.
function parseXml(text) {
  const decode = value => value.replace(/&(#x[0-9a-f]+|#\d+|amp|lt|gt|quot|apos);/gi, (_, entity) => {
    if (entity[0] === '#') return String.fromCodePoint(parseInt(entity.slice(entity[1] === 'x' || entity[1] === 'X' ? 2 : 1), entity[1] === 'x' || entity[1] === 'X' ? 16 : 10));
    return { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" }[entity.toLowerCase()];
  });
  const local = name => name.slice(name.indexOf(':') + 1);
  const document = { children: [] }, stack = [document];
  const token = /<!--[\s\S]*?-->|<\?[\s\S]*?\?>|<!\[CDATA\[([\s\S]*?)\]\]>|<!DOCTYPE[^>]*>|<\/([^\s>]+)\s*>|<([^\s/>]+)((?:\s+[^\s=]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?)>|([^<]+)/g;
  let position = 0;
  for (const match of text.matchAll(token)) {
    if (match.index !== position) throw new Error('Malformed XML');
    position = match.index + match[0].length;
    const [, cdata, close, open, attributeText, selfClosing, characters] = match;
    const current = stack.at(-1);
    if (close !== undefined) {
      if (stack.length < 2 || current.qualified !== close) throw new Error('Malformed XML');
      stack.pop();
    } else if (open !== undefined) {
      const attributes = {};
      for (const [, key, double, single] of attributeText.matchAll(/([^\s=]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g))
        if (!key.startsWith('xmlns')) attributes[key] = decode(double ?? single);
      const element = { name: local(open), qualified: open, attributes, text: '', children: [] };
      current.children.push(element);
      if (!selfClosing) stack.push(element);
    } else if (cdata !== undefined || characters !== undefined) {
      if (stack.length > 1 && current.children.length === 0) current.text += cdata ?? decode(characters);
    }
  }
  if (position !== text.length || stack.length !== 1 || document.children.length !== 1) throw new Error('Malformed XML');
  return document.children[0];
}
const child = (element, name) => element.children.find(item => item.name === name);
const descendants = (element, name) => element.children.flatMap(item => [...(item.name === name ? [item] : []), ...descendants(item, name)]);
const childText = (element, name) => { const found = child(element, name); return found ? found.text : null; };
const unique = names => { if (new Set(names).size !== names.length) throw new Error('Duplicate archive entries'); };

function npmMetadata(path) {
  const entries = tarEntries(path), names = entries.map(entry => entry.name);
  unique(names);
  const manifest = entries.find(entry => entry.name === 'package/package.json');
  if (!manifest) throw new Error(`${path} has no package/package.json`);
  const p = JSON.parse(utf8.decode(manifest.data));
  const repository = p.repository ?? {};
  const dependencies = {};
  for (const field of ['dependencies', 'optionalDependencies', 'peerDependencies'])
    for (const [key, value] of Object.entries(p[field] ?? {})) {
      if (Object.hasOwn(dependencies, key) && dependencies[key] !== value) throw new Error('Conflicting dependency ranges');
      dependencies[key] = value;
    }
  if (p.private) throw new Error('Private package');
  const readme = entries.find(entry => entry.name === 'package/README.md');
  return { name: p.name, version: p.version, repository: typeof repository === 'string' ? repository : repository.url ?? '',
    source: p.gitHead ?? '', dependencies, readme: readme ? utf8.decode(readme.data) : null };
}

function nugetMetadata(path) {
  const entries = zipEntries(path), names = entries.map(entry => entry.name);
  unique(names);
  const specs = entries.filter(entry => entry.name.endsWith('.nuspec') && !entry.name.includes('/'));
  if (specs.length !== 1) throw new Error('Expected one root nuspec');
  const metadata = child(parseXml(utf8.decode(specs[0].read()).replace(/^﻿/, '')), 'metadata');
  if (!metadata) throw new Error(`${path} has no nuspec metadata`);
  const repository = child(metadata, 'repository');
  const dependencies = {};
  for (const dependency of descendants(metadata, 'dependency')) {
    const { id, version } = dependency.attributes;
    if (id === undefined || version === undefined) throw new Error('A nuspec dependency needs an id and a version');
    if (Object.hasOwn(dependencies, id) && dependencies[id] !== version) throw new Error('Conflicting framework dependency ranges');
    dependencies[id] = version;
  }
  // NuGet adds a repository signature; compare every other file exactly when resuming.
  const content = Object.fromEntries(entries.filter(entry => entry.name !== '.signature.p7s')
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
    .map(entry => [entry.name, createHash('sha256').update(entry.read()).digest('hex')]));
  const readmePath = childText(metadata, 'readme');
  let readme = null;
  if (readmePath) {
    const entry = entries.find(item => item.name === readmePath.replaceAll('\\', '/'));
    if (!entry) throw new Error(`${path} does not contain its README ${readmePath}`);
    readme = utf8.decode(entry.read()).replace(/^﻿/, '');
  }
  return { name: childText(metadata, 'id'), version: childText(metadata, 'version'),
    repository: repository?.attributes.url ?? '', source: repository?.attributes.commit ?? '', dependencies, content, readme };
}

// The name, version, repository, source commit, dependencies and README of an npm or NuGet archive; a NuGet archive
// also lists the SHA-256 of every file except its repository signature.
export function inspect(path, registry) {
  return registry === 'npm' ? npmMetadata(path) : nugetMetadata(path);
}
