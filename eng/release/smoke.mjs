// A small public-registry consumer. CI already exercises the full template matrix.
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { VERSION } from './artifacts.mjs';
const directory = mkdtempSync(join(tmpdir(), 'runic-public-smoke-'));
const logs = resolve('artifacts/release');
mkdirSync(logs, {recursive: true});
process.env.NUGET_PACKAGES ??= resolve('.cache/nuget');
let log = '';
function run(command, args, cwd = directory) {
  console.log(`> ${command} ${args.join(' ')}`);
  log += `\n> ${command} ${args.join(' ')}\n`;
  try { log += execFileSync(command, args, {cwd, encoding: 'utf8', timeout: 180000, maxBuffer: 8 * 1024 * 1024}); }
  catch (error) { log += `${error.stdout ?? ''}\n${error.stderr ?? ''}`; throw new Error(`Public install smoke failed: ${command}`); }
}
try {
  writeFileSync(join(directory, 'NuGet.Config'), '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>');
  writeFileSync(join(directory, 'Smoke.csproj'), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Runic.CommandLine" Version="[${VERSION}]"/></ItemGroup></Project>`);
  writeFileSync(join(directory, 'Program.cs'), 'System.Console.WriteLine(typeof(Runic.CommandLine.CommandCatalog).FullName);');
  run('dotnet', ['run', '--project', 'Smoke.csproj', '-c', 'Release']);
  run('dotnet', ['tool', 'install', 'dotnet-runic', '--version', VERSION, '--tool-path', join(directory, 'tools'), '--configfile', join(directory, 'NuGet.Config')]);
  run(join(directory, 'tools', process.platform === 'win32' ? 'dotnet-runic.exe' : 'dotnet-runic'), ['--help']);
  writeFileSync(join(directory, 'package.json'), JSON.stringify({name: 'runic-public-smoke', private: true, type: 'module'}));
  run(process.platform === 'win32' ? 'npm.cmd' : 'npm', ['install', '--save-exact', '--no-audit', '--no-fund',
    '--registry=https://registry.npmjs.org', '--@runic-artifex:registry=https://registry.npmjs.org',
    `--cache=${process.env.npm_config_cache ?? resolve('.cache/npm')}`, `@runic-artifex/application-bridge@${VERSION}`]);
  run('bun', ['-e', 'const bridge = await import("@runic-artifex/application-bridge"); if (!Object.keys(bridge).length) throw Error("Empty bridge exports");']);
  console.log('Public .NET library, tool and npm bridge installation passed.');
} finally {
  writeFileSync(join(logs, 'public-smoke.log'), log);
  rmSync(directory, {recursive: true, force: true});
}
