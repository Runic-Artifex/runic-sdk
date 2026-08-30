#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { basename, join } from 'node:path';

export const RECEIPT_SCHEMA = 'runic.current-host-boundary/1';
const manifest = JSON.parse(await readFile(join(import.meta.dirname, '../current-host-transport/generated/bridge.manifest.json'), 'utf8'));
export const BRIDGE_CONTRACT = { identity: manifest.protocol.identity, version: manifest.protocol.version, fingerprint: manifest.contractFingerprint };
export const BRIDGE_MANIFEST_SHA256 = createHash('sha256').update(await readFile(join(import.meta.dirname, '../current-host-transport/generated/bridge.manifest.json'))).digest('hex');
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hash = async (path) => createHash('sha256').update(await readFile(path)).digest('hex');

function journey(receipt, label, errors) {
  if (!Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2 || !same(receipt.journeys[0], receipt.journeys[1])) {
    errors.push(label + ' repeat receipt is not deterministic');
    return undefined;
  }
  return receipt.journeys[0];
}

export function verifyReceiptLinks(hostReceipt, svelteReceipt, angularReceipt) {
  const errors = [];
  const host = journey(hostReceipt, 'host', errors);
  const svelte = journey(svelteReceipt, 'Svelte', errors);
  const angular = journey(angularReceipt, 'Angular', errors);
  if (host && !same({ identity: host.bridgeManifest?.protocol?.identity, version: host.bridgeManifest?.protocol?.version, fingerprint: host.bridgeManifest?.contractFingerprint }, BRIDGE_CONTRACT)) errors.push('host generated contract mismatch');
  for (const [label, receipt] of [['Svelte', svelte], ['Angular', angular]]) {
    if (receipt && !same(receipt.bridgeContract, BRIDGE_CONTRACT)) errors.push(label + ' generated contract mismatch');
    if (receipt && receipt.bridgeManifestSha256 !== BRIDGE_MANIFEST_SHA256) errors.push(label + ' generated manifest linkage mismatch');
  }
  if (host && svelte && angular && (!same(host.releaseManifest, svelte.releaseManifest) || !same(host.releaseManifest, angular.releaseManifest))) errors.push('release authority linkage mismatch');
  if (svelte && (!Array.isArray(svelte.candidates) || !svelte.candidates.some((item) => item.identity === '@runic-artifex/application-bridge') || !svelte.candidates.some((item) => item.identity === '@runic-artifex/svelte'))) errors.push('Svelte candidate linkage mismatch');
  if (angular && (!Array.isArray(angular.candidates) || !angular.candidates.some((item) => item.identity === '@runic-artifex/application-bridge') || !angular.candidates.some((item) => item.identity === '@runic-artifex/angular'))) errors.push('Angular candidate linkage mismatch');
  return { ok: errors.length === 0, errors };
}

export async function linkReceipts(hostPath, sveltePath, angularPath) {
  const [host, svelte, angular] = await Promise.all([hostPath, sveltePath, angularPath].map(async (path) => JSON.parse(await readFile(path, 'utf8'))));
  const report = verifyReceiptLinks(host, svelte, angular); if (!report.ok) throw new Error(report.errors.join('\n'));
  return { schema: RECEIPT_SCHEMA, bridgeContract: BRIDGE_CONTRACT, releaseManifest: host.journeys[0].releaseManifest, receipts: await Promise.all([hostPath, sveltePath, angularPath].map(async (path) => ({ name: basename(path), sha256: await hash(path) }))) };
}

async function main() { const [host, svelte, angular] = process.argv.slice(2); if (!host || !svelte || !angular || process.argv.length !== 5) throw new Error('Usage: node eng/current-host-boundary/verify.mjs <host-repeat.json> <svelte-repeat.json> <angular-repeat.json>'); process.stdout.write(JSON.stringify(await linkReceipts(host, svelte, angular), null, 2) + '\n'); }
if (import.meta.main) main().catch((error) => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });
