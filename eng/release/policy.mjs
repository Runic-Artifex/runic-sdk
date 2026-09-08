import assert from 'node:assert/strict';
export const automatedAndEngineeringGates = [
  'core-conformance', 'application-real-bridge', 'package-consumption', 'optional-provider-isolation',
  'performance-matched-baseline', 'soak-two-hours', 'independent-review', 'registry-ownership',
  ...['win-x64','linux-x64','osx-arm64'].flatMap(os=>[`native-jit-${os}`,`native-aot-${os}`]),
];
export const fullHumanGates = [
  'pilot-1', 'pilot-2',
  ...['windows','linux-x11','linux-wayland-portal','macos','macos-signed-sandbox'].map(os=>`interactive-native-${os}`),
  ...['nvda','voiceover','orca-x11','orca-wayland'].map(profile=>`accessibility-${profile}`),
];
export const demoHumanGates = ['interactive-native-linux-local', 'interactive-native-windows-vm'];
export const demoAuthorization = 'User authorized the demo preview to use actual local Linux and the available Windows VM, deferring other native human checks, accessibility profiles, and independent pilots. Deferred checks are not passes or verified support claims. User authorized a thirty-minute routine native soak for the demo preview; resource, operation, duration, shutdown and 20% memory-growth criteria remain required.';
export const demoMemoryTrendWaiver = {
  id: 'demo-native-memory-trend',
  failureCode: 'sustained-memory-trend',
  platform: 'linux', maximumQuarterGrowthBytes: 5*1024*1024, maximumQuarterGrowthRatio: 0.01,
  authorization: 'User explicitly accepted the small residual memory trend as a demo-preview nonblocker. Only Linux sustained-quarter memory trend with last-minus-first quarter growth at most 5 MiB AND 1% may be waived; duration, cycles, operations, resources, process shutdown and the 20% memory-growth bound remain required. Raw failed evidence remains failed.',
  attribution: 'A system-package contribution is suspected, not established as the sole cause. The patched thirty-minute run still showed small growth; this is not a verified leak fix or a full-v1 waiver.',
};
export function acceptancePolicy(profile = 'full-v1') {
  assert(['full-v1','demo-preview'].includes(profile), `Unknown acceptance profile: ${profile}`);
  return {
    schema: 'runic.preview-policy/1', profile,
    authorization: profile === 'demo-preview' ? demoAuthorization : 'Original complete preview acceptance plan.',
    required: [...automatedAndEngineeringGates.map(gate => profile === 'demo-preview' && gate === 'soak-two-hours' ? 'soak-thirty-minutes' : gate),...(profile === 'demo-preview' ? demoHumanGates : fullHumanGates)],
    ...(profile === 'demo-preview' ? {knownIssues:[{...demoMemoryTrendWaiver}]} : {}),
    deferred: profile === 'demo-preview' ? fullHumanGates.map(gate=>({gate,outcome:'deferred',reason:demoAuthorization})) : [],
  };
}
export function validatePolicy(policy) { assert.deepEqual(policy, acceptancePolicy(policy.profile), 'Policy differs from supported scope'); }
