// Focused local checks. GitHub CI owns the complete cross-platform workflow.
import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { root, run, configuration, workspace } from './run.mjs';
import { managedGroups, managedTests } from './ci/plan.mjs';
export function selection(scope) {
  if (managedGroups.includes(scope)) return {kind: 'managed', paths: managedTests().filter(p => p.group === scope).map(p => p.path)};
  if (scope === 'engineering') return {kind: 'bun', paths: ['', 'ci', 'release', 'reliability'].flatMap(dir =>
    readdirSync(resolve(root, 'eng', dir)).filter(name => name.endsWith('.test.mjs')).map(name => `./eng/${dir ? dir + '/' : ''}${name}`))};
  if (scope?.startsWith('web/')) {
    const item = workspace.npm.find(p => p.path === `packages/${scope}`);
    if (item && JSON.parse(readFileSync(resolve(root, item.path, 'package.json'))).scripts?.test)
      return {kind: 'web', paths: [item.path]};
  }
  if (scope?.endsWith('.csproj')) {
    const path = resolve(root, scope);
    const executable = /<OutputType>Exe<\/OutputType>/.test(readFileSync(path, 'utf8'));
    return {kind: executable ? 'managed' : 'dotnet-test', paths: [path]};
  }
  if (/\.test\.[cm]?[jt]s$/.test(scope ?? '')) {
    readFileSync(resolve(root, scope));
    return {kind: 'bun', paths: [resolve(root, scope)]};
  }
  throw new Error(`Unknown test scope: ${scope}. Use --list.`);
}
function main() {
  const [scope, ...extra] = process.argv.slice(2);
  if (!scope || scope === '--list' || scope === '--help') {
    console.log(`Choose a focused check (no tests have run):\n  bun run test <scope>\n\nManaged: ${managedGroups.join(', ')}\nEngineering: engineering or eng/release/contracts.test.mjs\nWeb: ${workspace.npm.filter(p => JSON.parse(readFileSync(resolve(root, p.path, 'package.json'))).scripts?.test).map(p => p.path.replace('packages/', '')).join(', ')}\nSingle .NET suite: tests/dotnet/<name>/<name>.csproj\nFull workflow (optional locally): bun run ci`);
    return;
  }
  const selected = selection(scope);
  process.env.NUGET_PACKAGES ??= resolve(root, '.cache/nuget');
  process.env.RUNIC_BRIDGE_INSPECTOR = resolve(root, `tools/Runic.Application.Bridge.Inspector/bin/${configuration}/net10.0/Runic.Application.Bridge.Inspector.dll`);
  if (selected.kind === 'bun') run('bun', ['test', '--timeout', '180000', ...selected.paths, ...extra]);
  else if (selected.kind === 'web') {
    run('bun', ['eng/run.mjs', 'build-web']);
    run('bun', ['run', 'test', ...extra], resolve(root, selected.paths[0]));
  } else for (const path of selected.paths) {
    if (selected.kind === 'managed') run('dotnet', ['run', '--project', path, '-c', configuration, ...(extra.length ? ['--', ...extra] : [])]);
    else run('dotnet', ['test', path, '-c', configuration, ...extra]);
  }
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
