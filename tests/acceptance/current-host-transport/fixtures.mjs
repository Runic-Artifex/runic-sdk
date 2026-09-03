import { readFileSync } from 'node:fs';

const protocol = 'runic.artifex.setup';
const contractFingerprint = JSON.parse(readFileSync(
  new URL('./generated/bridge.ir.json', import.meta.url),
  'utf8',
)).fingerprint.value;

export default {
  'initialize.client.json': { protocol, version: 1, contractFingerprint, connectionEpoch: 0, kind: 'initialize', commandId: '00000000-0000-4000-8000-000000000001', payload: { _tag: 'InitializeApplication' } },
  'resynchronized.host.json': { protocol, version: 1, contractFingerprint, connectionEpoch: 1, kind: 'snapshot', sessionId: '11111111-1111-4111-8111-111111111111', sequence: 1, revision: 0, commandId: '00000000-0000-4000-8000-000000000002', payload: { viewId: 'Welcome', revision: 0, selectedFeatures: [], canNavigateBack: false, canNavigateNext: true } },
  'late-old-admission-error.host.json': { protocol, version: 1, contractFingerprint, connectionEpoch: 0, kind: 'error', sessionId: '11111111-1111-4111-8111-111111111111', sequence: 0, revision: 0, commandId: '00000000-0000-4000-8000-000000000002', payload: { _tag: 'CommandRejected', message: 'old epoch', retryable: true } },
  'future-admission-error.host.json': { protocol, version: 1, contractFingerprint, connectionEpoch: 2, kind: 'error', sessionId: '11111111-1111-4111-8111-111111111111', sequence: 0, revision: 0, commandId: '00000000-0000-4000-8000-000000000002', payload: { _tag: 'CommandRejected', message: 'future epoch', retryable: true } },
};
