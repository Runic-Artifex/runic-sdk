// The consumer scenarios of the train gate (eng/train/README.md). Each one builds or inspects real consumers of
// one combination of Command Line, Translations and runic-sdk packages, with warnings as errors, outside the
// repository (no Directory.Build.props, central package management or global.json of runic-sdk applies).
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bundledVersions, nupkgTexts, writeNugetConfig } from './nuget.mjs';
import { keyCoverage, keysFromJson, keysFromMarkdown, lintLockstep, lintRanges, lintTemplatePins, lintToolPins, rmf2MessageNames } from './checks.mjs';
import { compareVersions } from './versions.mjs';

export const scenarios = [
  { id: 'S1', title: 'Command Line app in German through Translations.CommandLine, NativeAOT', required: true, run: commandLineInGerman },
  { id: 'S2', title: 'Framework key coverage of the German Command Line catalog', required: true, run: keyCoverageScenario },
  { id: 'S3', title: 'runic-app --translations --tests with Hosting, Views.Translations and a German Command Line front end', required: true, run: desktopTemplate },
  { id: 'S4', title: 'runic-app --host cswebui --frontend svelte --translations --tests', required: true, run: csWebUiTemplate },
  { id: 'S5', title: "Tools of the template's manifest: dotnet-runic and dotnet-runic-translations", required: true, run: tools },
  { id: 'S6', title: 'Version ranges and pins of every candidate package', required: true, run: ranges },
  { id: 'S7', title: 'WPF consumer restore and build (EnableWindowsTargeting, Linux)', required: true, run: wpf },
  { id: 'S8', title: 'Translations editor built against the runic-sdk candidate (informational)', required: false, run: editor },
];

// ---- running commands ---------------------------------------------------------------------------------------------

const ERROR_LINE = /\b(error|warning) [A-Z]{2,}\d{3,}:|\berror\b.*:|Failed!|failed with \d+ error/;

// Distinct error lines of a build log, without the project path that prefixes each MSBuild message.
export function errorLines(output) {
  const lines = output.split(/\r?\n/).filter(line => ERROR_LINE.test(line) && !/^\s*\d+ (Warning|Error)\(s\)/.test(line));
  return [...new Set(lines.map(line => line.replace(/^.*?:\s(?=(error|warning) )/, '').replace(/\s+\[[^\]]+\]$/, '').trim()))]
    .filter(line => line.length).slice(0, 25);
}

export function createRunner(context) {
  let index = 0;
  return function run(scenario, command, args, { cwd = context.work, env = {}, timeout = 20 * 60_000, input } = {}) {
    const log = join(context.logs, `${scenario}-${String(++index).padStart(2, '0')}-${[command, args[0]].join('-').replace(/[^\w.-]+/g, '_')}.log`);
    const started = Date.now();
    const result = spawnSync(command, args, { cwd, env: { ...context.env, ...env }, encoding: 'utf8', timeout, input, maxBuffer: 512 * 1024 * 1024 });
    const output = `${result.stdout ?? ''}${result.stderr ?? ''}`;
    writeFileSync(log, `$ ${command} ${args.join(' ')}\n(cwd ${cwd}, ${Math.round((Date.now() - started) / 1000)} s, exit ${result.status ?? result.signal})\n\n${output}`);
    console.log(`  ${command} ${args.join(' ')} -> ${result.status ?? result.signal} (${Math.round((Date.now() - started) / 1000)} s)`);
    return { code: result.status ?? -1, stdout: result.stdout ?? '', stderr: result.stderr ?? '', output, log, error: result.error };
  };
}

// dotnet with warnings as errors. When every error of a failed build is an active expected failure that names a
// waiver (I1's NU1608), the failures are recorded and the scenario continues with that warning waived, so the rest
// of the scenario still checks something.
function dotnet(context, scenario, check, args, options = {}) {
  const waived = context.waived[scenario] ??= new Set();
  const withWaivers = () => waived.size ? [...args, `-p:RunicTrainWaive=${[...waived].join('%3B')}`] : args;
  let result = context.run(scenario, 'dotnet', withWaivers(), options);
  if (result.code === 0) return result;
  let errors = errorLines(result.output);
  if (!errors.length) errors = [`dotnet ${args[0]} failed with exit code ${result.code}; see ${result.log}`];
  const failures = errors.map(message => ({ scenario, check, message }));
  for (const failure of failures) context.fail(failure.scenario, failure.check, failure.message);
  const entries = failures.map(failure => context.expectedFor(failure));
  const codes = entries.every(entry => entry?.waive?.length) ? new Set(entries.flatMap(entry => entry.waive)) : null;
  if (codes && [...codes].some(code => !waived.has(code))) {
    for (const code of codes) waived.add(code);
    context.note(scenario, `Continuing with ${[...codes].join(', ')} waived after the expected failure ${[...new Set(entries.map(entry => entry.id))].join(', ')}.`);
    result = context.run(scenario, 'dotnet', withWaivers(), options);
    if (result.code === 0) return result;
    const more = errorLines(result.output).filter(message => !errors.includes(message));
    for (const message of more.length ? more : [`dotnet ${args[0]} failed with exit code ${result.code}; see ${result.log}`])
      context.fail(scenario, check, message);
  }
  return null;
}

// Lines of framework text in English in output that should be German.
// Command Line 0.12 rewords usage errors ("The option '--x' is not recognized."); both wordings count.
const ENGLISH = /^(Run "|Usage:|Unknown option|The option '|The command '|Options:|Arguments:|Commands:|Error:|A required argument)|for more information|is not recognized/;
function checkGerman(context, scenario, output) {
  for (const line of new Set(output.split(/\r?\n/).map(item => item.trim()).filter(item => ENGLISH.test(item))))
    context.fail(scenario, 'german', `English framework text in German output: ${line}`);
}

const write = (path, text) => { mkdirSync(join(path, '..'), { recursive: true }); writeFileSync(path, text); };

// ---- S1 -------------------------------------------------------------------------------------------------------

async function commandLineInGerman(context) {
  const { cli, translations } = context.versions;
  const directory = join(context.work, 'cli-de');
  write(join(directory, 'translations/runic.json'), JSON.stringify({ schemaVersion: 1, catalog: 'cli-de',
    code: { namespace: 'CliDe.Translations', className: 'CliText', visibility: 'internal' }, baseLocale: 'en', locales: ['en', 'de'] }, null, 2));
  write(join(directory, 'translations/en.rmf2'), 'commands {\n  greet = Greets a person\n}\napp {\n  greeting =\n    .input {$name :string}\n    {{Hello {$name}}}\n}\n');
  write(join(directory, 'translations/de.rmf2'), 'commands {\n  greet = Grüßt eine Person\n}\napp {\n  greeting =\n    .input {$name :string}\n    {{Hallo {$name}}}\n}\n');
  write(join(directory, 'CliDe.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <IlcTreatWarningsAsErrors>true</IlcTreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Runic.CommandLine" Version="${cli}" />
    <PackageReference Include="Runic.Translations" Version="${translations}" />
    <PackageReference Include="Runic.Translations.Build" Version="${translations}" PrivateAssets="all" />
    <PackageReference Include="Runic.Translations.CommandLine" Version="${translations}" />
    <RunicTranslationProject Include="translations/runic.json" />
  </ItemGroup>
</Project>
`);
  write(join(directory, 'Program.cs'), `using System.Globalization;
using CliDe.Translations;
using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.Translations;
using Runic.Translations.CommandLine;

CultureInfo culture = CultureInfo.GetCultureInfo(Environment.GetEnvironmentVariable("CLI_CULTURE") ?? "en");
ITranslationManager manager = await CliTextCatalog.CreateManagerAsync(culture.Name);
Commands.Text = new CliText(manager);
return await new CommandApp(GeneratedCommandCatalog.Create())
{
    Name = "clide",
    Culture = culture,
    TextResolver = new TranslationCommandTextResolver(manager),
}.RunAsync(args);

internal static class Commands
{
    internal static CliText Text { get; set; } = null!;

    [Command("greet", Description = "Greet a person", DescriptionKey = "commands.greet")]
    public static string Greet([Argument(Description = "Name")] string name) => Text.Messages.app_greeting(name: name);
}
`);
  const out = join(directory, 'out');
  if (!dotnet(context, 'S1', 'build', ['publish', directory, '-c', 'Release', '-r', 'linux-x64', '-o', out])) return;
  const app = join(out, 'CliDe');
  const german = { env: { CLI_CULTURE: 'de' } };
  const greet = context.run('S1', app, ['greet', 'Ada'], german);
  if (greet.code !== 0 || greet.stdout.trim() !== 'Hallo Ada') context.fail('S1', 'run', `greet Ada printed '${greet.output.trim()}' with exit code ${greet.code}; expected 'Hallo Ada' and 0`);
  const unknown = context.run('S1', app, ['greet', '--unknown'], german);
  if (unknown.code !== 2) context.fail('S1', 'exit-code', `greet --unknown exited ${unknown.code}; expected 2 (usage error)`);
  // Translations 0.9.1's adapter said "Unbekannte Option"; Command Line 0.12's catalog says "Die Option '--x' ist unbekannt."
  if (!/Unbekannte Option|Die Option '[^']*' ist unbekannt/.test(unknown.output)) context.fail('S1', 'german', `greet --unknown has no German diagnostic: ${unknown.output.trim().split('\n')[0]}`);
  checkGerman(context, 'S1', unknown.output);
  const help = context.run('S1', app, ['greet', '--help'], german);
  if (help.code !== 0) context.fail('S1', 'exit-code', `greet --help exited ${help.code}; expected 0`);
  checkGerman(context, 'S1', help.output);
  context.note('S1', `NativeAOT publish and German output checked; the --format json envelope and per-outcome exit codes join once Command Line 0.12.0 defines them (runic-cli-sdk#88, #89).`);
}

// ---- S2 -------------------------------------------------------------------------------------------------------

async function keyCoverageScenario(context) {
  const { cli } = context.versions;
  const commandLine = await context.packageFile('cli', 'Runic.CommandLine');
  const json = commandLine && nupkgTexts(commandLine, /(^|\/)text-keys\.json$/)[0];
  let keys;
  if (json) {
    keys = keysFromJson(json.text);
    context.note('S2', `Key list: ${json.name} in Runic.CommandLine ${cli} (${keys.required.size} keys, ${keys.removed.size} removed).`);
  } else {
    if (compareVersions(cli, '0.12.0') >= 0) context.fail('S2', 'key-list', `Runic.CommandLine ${cli} ships no text-keys.json (D061.19)`);
    const source = context.sources.cli;
    const ref = source.kind === 'run' ? source.run.headSha : `v${cli}`;
    const url = `https://raw.githubusercontent.com/${source.repository}/${ref}/docs/guides/command-line/text-keys.md`;
    const response = await fetch(url);
    if (!response.ok) { context.fail('S2', 'key-list', `No key list: Runic.CommandLine ${cli} has no text-keys.json and ${url} returned ${response.status}`); return; }
    keys = keysFromMarkdown(await response.text());
    context.note('S2', `Runic.CommandLine ${cli} ships no text-keys.json (D061.19 adds it in 0.12.0; runic-cli-sdk train/cli-catalog). `
      + `Using the key table of docs/guides/command-line/text-keys.md at ${ref} instead (${keys.required.size} keys).`);
  }
  const adapter = await context.packageFile('translations', 'Runic.Translations.CommandLine');
  const catalog = adapter && nupkgTexts(adapter, /(^|\/)runic-command-line\/de\.rmf2$/)[0];
  if (!catalog) { context.fail('S2', 'catalog', `Runic.Translations.CommandLine ${context.versions.translations} packs no runic-command-line/de.rmf2`); return; }
  const coverage = keyCoverage(keys, rmf2MessageNames(catalog.text));
  for (const key of coverage.missing) context.fail('S2', 'missing', `No German message for ${key}`);
  for (const key of coverage.stale) context.fail('S2', 'removed', `German message for the removed key ${key}`);
  for (const name of coverage.unknown) context.fail('S2', 'unknown', `German message ${name} matches no Command Line key`);
}

// ---- template applications (S3, S4, S5) ----------------------------------------------------------------------

async function templates(context) {
  if (context.templateHive) return context.templateHive;
  const file = await context.packageFile('sdk', 'Runic.Application.Templates');
  const hive = join(context.work, 'template-hive');
  const result = context.run('templates', 'dotnet', ['new', 'install', file, '--debug:custom-hive', hive]);
  if (result.code !== 0) throw new Error(`dotnet new install Runic.Application.Templates ${context.versions.sdk} failed; see ${result.log}`);
  return (context.templateHive = hive);
}

async function templateApp(context, scenario, name, extra) {
  const hive = await templates(context);
  const output = join(context.work, name);
  const created = context.run(scenario, 'dotnet', ['new', 'runic-app', '-n', name, '-o', output, '--debug:custom-hive', hive, ...extra]);
  if (created.code !== 0) { context.fail(scenario, 'create', `dotnet new runic-app ${extra.join(' ')} failed: ${errorLines(created.output)[0] ?? created.log}`); return null; }
  // A runic-sdk CI candidate's npm packages come from a local registry; the template's npm lock is bound to their
  // integrities exactly as runic-sdk's template tests do (tests/templates).
  if (context.npmRegistry && existsSync(join(output, 'Frontend/package.json'))) {
    const frontend = join(output, 'Frontend');
    context.run(scenario, 'npm', ['config', 'set', '--location=project', '@runic-artifex:registry', context.npmRegistry.url], { cwd: frontend });
    context.run(scenario, 'npm', ['config', 'set', '--location=project', 'fetch-timeout', '60000'], { cwd: frontend });
    const bound = context.run(scenario, 'bun', [join(context.root, 'tests/templates/bind-template-candidate-integrities.mjs'), join(frontend, 'package-lock.json'), ...context.npmRegistry.archives],
      { env: { RUNIC_TEMPLATE_NPM_REGISTRY: context.npmRegistry.url } });
    if (bound.code !== 0) { context.fail(scenario, 'npm', `The template's npm lock does not match the runic-sdk npm candidates: ${bound.output.trim().split('\n').at(-1)}`); return null; }
  }
  return output;
}

function buildAndTest(context, scenario, output, name) {
  if (!dotnet(context, scenario, 'build', ['build'], { cwd: output })) return false;
  return Boolean(dotnet(context, scenario, 'test', ['test', `${name}.Tests`], { cwd: output }));
}

function replaceOnce(text, search, replacement, file) {
  if (!text.includes(search)) throw new Error(`The template's ${file} changed: '${search.split('\n')[0]}' not found. Update eng/train/scenarios.mjs.`);
  return text.replace(search, replacement);
}

// The README patterns of Views.Translations and Translations.Hosting, plus a `cli` front end in German.
function addHostingAndCommandLine(output, name, versions) {
  const project = join(output, `${name}.csproj`);
  const anchor = '<PackageReference Include="Runic.Translations.Build"';
  writeFileSync(project, replaceOnce(readFileSync(project, 'utf8'), anchor, `<PackageReference Include="Runic.Translations.Hosting" Version="${versions.translations}" />
    <PackageReference Include="Runic.Translations.CommandLine" Version="${versions.translations}" />
    <PackageReference Include="Runic.Application.Views.Translations" Version="${versions.sdk}" />
    <PackageReference Include="Runic.CommandLine" Version="${versions.cli}" />
    ${anchor}`, `${name}.csproj`));
  const servicesPath = join(output, 'WorkspaceServices.cs');
  let services = readFileSync(servicesPath, 'utf8');
  services = replaceOnce(services, 'using Runic.Translations;', 'using Runic.Translations;\nusing Runic.Translations.Hosting;\nusing Runic.Application.Views.Translations;', 'WorkspaceServices.cs');
  const start = services.indexOf('        services.AddSingleton(provider =>'), end = services.indexOf('        return services;');
  if (start < 0 || end < start) throw new Error("The template's WorkspaceServices.cs changed: no translation registration to replace. Update eng/train/scenarios.mjs.");
  services = `${services.slice(0, start)}        services.AddRunicTranslations().AddCatalog(AppTextCatalog.Registration, context => new AppText(context));
        services.AddRunicViewsTranslations();
${services.slice(end)}`;
  services = services.replaceAll('GetRequiredService<TranslationContext>()', 'GetRequiredService<ITranslationContext>()')
    .replaceAll('SupportedLanguage(TranslationContext translations', 'SupportedLanguage(ITranslationContext translations');
  services = replaceOnce(services, 'await services.GetRequiredService<IRunicApplicationLocale>().SetLocaleAsync',
    'var locale = services.GetRequiredService<IRunicApplicationLocale>();\n        locale.Follow(translations.SetLocaleAsync);\n        await locale.SetLocaleAsync', 'WorkspaceServices.cs');
  writeFileSync(servicesPath, services);
  const programPath = join(output, 'Program.cs');
  writeFileSync(programPath, replaceOnce(readFileSync(programPath, 'utf8'), `using ${name};\n`,
    `using ${name};\n\nif (args is ["cli", ..]) return await ${name}.Cli.RunAsync(args[1..]);\n`, 'Program.cs'));
  writeFileSync(join(output, 'Cli.cs'), `using System.Globalization;
using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.Translations;
using Runic.Translations.CommandLine;

namespace ${name};

public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var culture = CultureInfo.GetCultureInfo(Environment.GetEnvironmentVariable("RUNIC_LOCALE") ?? "en");
        ITranslationSnapshot snapshot = await AppTextCatalog.CreateProvider().GetSnapshotAsync(culture.Name);
        return await new CommandApp(GeneratedCommandCatalog.Create())
        {
            Name = "desktop",
            Culture = culture,
            TextResolver = new TranslationCommandTextResolver(snapshot),
        }.RunAsync(args);
    }

    [Command("hello", Description = "Say hello")]
    public static string Hello([Argument(Description = "Name")] string name) => $"Hello {name}";
}
`);
}

async function desktopTemplate(context) {
  const name = 'DesktopApp';
  const output = await templateApp(context, 'S3', name, ['--translations', 'true', '--tests', 'true']);
  if (!output) return;
  const restored = context.run('S3', 'dotnet', ['tool', 'restore'], { cwd: output });
  if (restored.code !== 0) { context.fail('S3', 'tools', `dotnet tool restore failed: ${errorLines(restored.output)[0] ?? restored.log}`); return; }
  addHostingAndCommandLine(output, name, context.versions);
  const built = buildAndTest(context, 'S3', output, name);
  const app = join(output, `bin/Debug/net10.0/${name}.dll`);
  if (!built && !existsSync(app)) return;
  const missing = context.run('S3', 'dotnet', [app, 'cli', 'hello'], { env: { RUNIC_LOCALE: 'de' } });
  // Translations 0.9.1's adapter said "Ein erforderliches Argument fehlt"; Command Line 0.12's catalog says "Das Argument name fehlt."
  if (missing.code !== 2 || !/Ein erforderliches Argument fehlt|Das Argument \S+ fehlt/.test(missing.output))
    context.fail('S3', 'german', `cli hello without its argument: exit code ${missing.code} and '${missing.output.trim().split('\n')[0]}'; expected 2 and the German missing-argument diagnostic`);
  checkGerman(context, 'S3', missing.output);
}

async function csWebUiTemplate(context) {
  const name = 'WebApp';
  const output = await templateApp(context, 'S4', name, ['--host', 'cswebui', '--frontend', 'svelte', '--translations', 'true', '--tests', 'true']);
  if (!output) return;
  const restored = context.run('S4', 'dotnet', ['tool', 'restore'], { cwd: output });
  if (restored.code !== 0) { context.fail('S4', 'tools', `dotnet tool restore failed: ${errorLines(restored.output)[0] ?? restored.log}`); return; }
  buildAndTest(context, 'S4', output, name);
}

async function tools(context) {
  const { sdk, translations } = context.versions;
  const output = await templateApp(context, 'S5', 'ToolsApp', ['--translations', 'true']);
  if (!output) return;
  const manifest = JSON.parse(readFileSync(join(output, '.config/dotnet-tools.json'), 'utf8').replace(/^\s*\/\/.*$/gm, ''));
  for (const [tool, expected] of [['dotnet-runic', sdk], ['dotnet-runic-translations', translations]])
    if (manifest.tools?.[tool]?.version !== expected)
      context.fail('S5', 'manifest', `The template's tool manifest pins ${tool} ${manifest.tools?.[tool]?.version ?? '(none)'}; this train's is ${expected} (R5)`);
  const restored = context.run('S5', 'dotnet', ['tool', 'restore'], { cwd: output });
  if (restored.code !== 0) { context.fail('S5', 'restore', `dotnet tool restore failed: ${errorLines(restored.output)[0] ?? restored.log}`); return; }
  for (const [command, expected] of [['dotnet-runic', sdk], ['runic-translations', translations]]) {
    const result = context.run('S5', 'dotnet', ['tool', 'run', command, '--version'], { cwd: output });
    if (result.code !== 0 || !result.output.split(/\s+/).includes(expected))
      context.fail('S5', 'run', `dotnet tool run ${command} --version: exit code ${result.code}, '${result.output.trim().split('\n')[0]}'; expected ${expected}`);
  }
  // doctor checks the restored Runic package set (compatibility-set); what the runner lacks, such as WebKitGTK for
  // desktop-runtime, is the machine's business, not the train's.
  if (!dotnet(context, 'S5', 'restore', ['restore'], { cwd: output })) return;
  const doctor = context.run('S5', 'dotnet', ['tool', 'run', 'dotnet-runic', 'doctor'], { cwd: output });
  const failed = [...doctor.output.matchAll(/^FAIL ([\w-]+): (.*)$/gm)];
  for (const [, check, message] of failed)
    if (machineChecks.has(check)) context.note('S5', `doctor ${check} (this machine, not the train): ${message}`);
    else context.fail('S5', 'doctor', `dotnet runic doctor ${check}: ${message}`);
  if (doctor.code !== 0 && !failed.length) context.fail('S5', 'doctor', `dotnet runic doctor exited ${doctor.code}: ${doctor.output.trim().split('\n').slice(-2).join(' | ')}`);
  if (!/^PASS compatibility-set/m.test(doctor.output)) context.fail('S5', 'doctor', `dotnet runic doctor did not pass compatibility-set: ${(/^\w+ compatibility-set.*$/m.exec(doctor.output) ?? ['no result'])[0]}`);
}

// doctor checks that describe the machine rather than the packages.
const machineChecks = new Set(['javascript-runtime', 'package-manager', 'desktop-runtime', 'platform-services', 'browser']);

// ---- S6 -------------------------------------------------------------------------------------------------------

async function ranges(context) {
  const { cli, translations } = context.versions;
  const failures = [...lintLockstep(context.sources), ...lintRanges(context.sources)];
  const template = await context.packageFile('sdk', 'Runic.Application.Templates');
  if (template) {
    const pins = lintTemplatePins(nupkgTexts(template, /\.csproj$|dotnet-tools\.json$/), translations);
    failures.push(...pins.failures);
    if (!pins.pins.length) failures.push({ check: 'template-pin', message: 'Runic.Application.Templates pins no Runic.Translations version; update lintTemplatePins' });
  }
  const tools = [];
  for (const [repo, id] of [['sdk', 'dotnet-runic'], ['sdk', 'Runic.Create'], ['sdk', 'Runic.Assets'], ['translations', 'dotnet-runic-translations']]) {
    const file = await context.packageFile(repo, id);
    if (file) tools.push({ id, version: context.sources[repo].packages.find(item => item.id === id).version, bundled: bundledVersions(file, 'Runic.CommandLine') });
  }
  failures.push(...lintToolPins(tools, cli));
  for (const failure of failures) context.fail('S6', failure.check, failure.message);
  const count = Object.values(context.sources).reduce((sum, source) => sum + source.packages.length, 0);
  context.note('S6', `Checked ${count} packages; tools bundling Runic.CommandLine: ${tools.map(tool => `${tool.id} ${tool.bundled.join('/') || '-'}`).join(', ')}.`);
}

// ---- S7 -------------------------------------------------------------------------------------------------------

async function wpf(context) {
  const { cli, translations, sdk } = context.versions;
  const directory = join(context.work, 'wpf');
  write(join(directory, 'WpfConsumer.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Runic.Application.Views.Wpf" Version="${sdk}" />
    <PackageReference Include="Runic.Application.Views.Translations" Version="${sdk}" />
    <PackageReference Include="Runic.Translations" Version="${translations}" />
    <PackageReference Include="Runic.Translations.Hosting" Version="${translations}" />
    <PackageReference Include="Runic.Translations.Wpf" Version="${translations}" />
    <PackageReference Include="Runic.CommandLine" Version="${cli}" />
  </ItemGroup>
</Project>
`);
  write(join(directory, 'Wiring.cs'), `using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.Translations;
using Runic.Translations.Hosting;

namespace WpfConsumer;

public static class Wiring
{
    public static IServiceCollection AddTranslations(IServiceCollection services)
    {
        services.AddRunicTranslations();
        services.AddRunicViewsTranslations();
        return services;
    }
}
`);
  dotnet(context, 'S7', 'build', ['build', directory, '-c', 'Release']);
}

// ---- S8 (informational) -------------------------------------------------------------------------------------

async function editor(context) {
  const source = context.sources.translations;
  const ref = source.kind === 'run' ? source.run.headSha : `v${context.versions.translations}`;
  const checkout = join(context.work, 'runic-translations-sdk');
  mkdirSync(checkout, { recursive: true });
  for (const args of [['init', '-q'], ['fetch', '-q', '--depth', '1', `https://github.com/${source.repository}.git`, ref], ['checkout', '-q', 'FETCH_HEAD']]) {
    const result = context.run('S8', 'git', args, { cwd: checkout });
    if (result.code !== 0) { context.fail('S8', 'checkout', `git ${args[0]} ${ref} failed: ${result.output.trim().split('\n').at(-1)}`); return; }
  }
  const cswebui = context.sources.sdk.packages.find(item => item.id === 'Runic.Application.Views.CsWebUi')?.version;
  // Only the SDK comes from the candidate; the editor keeps its own Command Line pin, and builds Translations from source.
  const config = join(context.work, 'nuget.editor.config');
  writeNugetConfig(config, { cli: null, translations: null, sdk: context.feeds.sdk });
  dotnet(context, 'S8', 'build', ['build', 'apps/translations-editor/Runic.Translations.Editor.csproj', '-c', 'Release',
    `-p:RunicSdkDependencyVersion=${context.versions.sdk}`, `-p:RunicSdkCsWebUiDependencyVersion=${cswebui}`,
    '-p:RunicApplicationFrontendInstallEnabled=false', '-p:RunicApplicationFrontendBuildEnabled=false',
    `-p:RestoreConfigFile=${config}`, '-p:RestoreLockedMode=false'], { cwd: checkout });
  context.note('S8', `Translations editor at ${ref} with runic-sdk ${context.versions.sdk} (Views.CsWebUi ${cswebui}); informational, never fails the gate.`);
}
