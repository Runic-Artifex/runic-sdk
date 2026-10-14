// Runtime messages go through ILogger when the application has an ILoggerFactory (#249). Direct
// System.Diagnostics.Trace output in packages/dotnet is reserved for the fallback loggers, which
// write to Trace only when there is no factory, and for the explicit allow-list below.
import { test, expect } from 'bun:test';
import { readFileSync, readdirSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

const root = resolve(import.meta.dir, '..');
const skippedDirectories = new Set(['bin', 'obj', 'node_modules', 'dist', '.cache']);
// Trace.TraceError(, Trace.WriteLine(, System.Diagnostics.Trace.Fail( and so on, also split across lines.
const traceCall = /\bTrace\s*\.\s*(Trace\w+|Write\w*|Fail|Assert)\s*\(/g;
// using T = System.Diagnostics.Trace; and using static System.Diagnostics.Trace; hide calls from the scan.
const traceImport = /\busing\s+(static\s+)?(\w+\s*=\s*)?(global::)?System\s*\.\s*Diagnostics\s*\.\s*Trace\s*;/g;

// The fallback loggers: ILogger implementations that write to Trace only when there is no ILoggerFactory.
export const fallbackLoggers = [
  ['packages/dotnet/Runic.Application.Views/RunicViewsTelemetry.cs', 'TraceFallbackLogger'],
  ['packages/dotnet/Runic.Application.Views.Desktop/LentLoggerFactory.cs', 'DesktopTraceFallbackLogger'],
  ['packages/dotnet/Runic.Application.Views.ReactiveUI/ReactiveLog.cs', 'TraceLogger'],
  ['packages/dotnet/Runic.Application.Views.Wpf/WpfViewsLog.cs', 'TraceFallbackLogger'],
  ['packages/dotnet/Runic.Navigation/NavigationTelemetry.cs', 'TraceFallbackLogger'],
  ['packages/dotnet/Runic.Navigation.Wpf/WpfNavigationLog.cs', 'TraceFallbackLogger'],
].map(([file, type]) => ({ file, type }));

// Direct calls outside a fallback logger, by file and a fragment of the call with its whitespace collapsed. Each
// entry matches exactly one call: the no-logger branch of a call that uses the logger first. Runic.Desktop has no
// fallback logger: without DesktopHostOptions.LoggerFactory these keep their earlier Trace text, which
// Runic.Desktop.Tests reads (BrowserHostTests.StalledLaunchListener), and the shutdown notice stays at Information.
export const traceAllowList = [
  ...[
    'TraceInformation($"{reason} received; closing Desktop windows.',
    'TraceError($"Closing Desktop windows for shutdown failed: {error}")',
    'TraceWarning($"{diagnostic.Code}: {diagnostic.Message}")',
  ].map(fragment => ({ file: 'packages/dotnet/Runic.Desktop/DesktopHost.cs', fragment })),
  ...[
    'TraceError($"A window close cancellation callback failed: {error}")',
    'TraceError($"Window close confirmation failed; the window was kept open: {error}")',
  ].map(fragment => ({ file: 'packages/dotnet/Runic.Desktop/Internal/WindowCloseController.cs', fragment })),
  ...[
    'TraceWarning($"{BridgeHandshakeExpiredCode}: Bridge WebSocket {connectionId} sent no token check',
    'TraceWarning("A generated browser profile could not be deleted; it remains in the temporary folder.")',
    'TraceWarning($"The diagnostic sink failed for {BrowserLaunchStalledCode}',
    'TraceWarning($"{BrowserLaunchStalledCode}: {message}")',
  ].map(fragment => ({ file: 'packages/dotnet/Runic.Desktop/WebUiWindow.cs', fragment })),
  // The public DesktopBridgeTransport(surface) constructor takes no logger; Runic's hosts pass one.
  { file: 'packages/dotnet/Runic.Application.Views.Desktop/DesktopBridgeTransport.cs',
    fragment: 'TraceError($"Bridge snapshot delivery for {name} failed: {error}")' },
];

function* sources(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) { if (!skippedDirectories.has(entry.name)) yield* sources(path); }
    else if (entry.name.endsWith('.cs')) yield path;
  }
}

const blank = text => text.replace(/[^\n]/g, ' ');

// Blanks comments, keeping offsets and line breaks. A // inside a string literal is not a comment.
function withoutComments(text) {
  return text.replace(/\/\*[\s\S]*?\*\//g, blank).split('\n').map(line => {
    for (let index = line.indexOf('//'); index >= 0; index = line.indexOf('//', index + 2)) {
      if ((line.slice(0, index).match(/"/g)?.length ?? 0) % 2 === 0) return line.slice(0, index) + blank(line.slice(index));
    }
    return line;
  }).join('\n');
}

// The type declarations of a file and the offsets of their bodies, found by brace depth on code without string
// and character literals, so the innermost type around a call is known, including nested types, records and structs.
const withoutLiterals = code => code.replace(/'(\\.|[^'\\\n])'/g, blank).replace(/"(\\.|[^"\\\n])*"/g, blank);

function typeBodies(skeleton) {
  const declaration = /\b(class|record|struct|interface)\s+(\w+)/g;
  const bodies = [];
  const open = [];
  let pending = null;
  let match = declaration.exec(skeleton);
  for (let offset = 0; offset < skeleton.length; offset++) {
    while (match && match.index <= offset) {
      pending = match[2];
      match = declaration.exec(skeleton);
    }
    const character = skeleton[offset];
    if (character === '{') {
      open.push({ name: pending, start: offset });
      pending = null;
    } else if (character === '}') {
      const body = open.pop();
      if (body?.name) bodies.push({ name: body.name, start: body.start, end: offset });
    } else if (character === ';' && pending) pending = null; // record R(...); or a primary-constructor type without a body
  }
  return bodies;
}

const calls = [];
const imports = [];
for (const path of sources(join(root, 'packages/dotnet'))) {
  const file = relative(root, path).replaceAll('\\', '/');
  const code = withoutComments(readFileSync(path, 'utf8'));
  const lineOf = offset => code.slice(0, offset).split('\n').length;
  for (const match of code.matchAll(traceImport)) imports.push(`${file}:${lineOf(match.index)}: ${match[0]}`);
  const skeleton = withoutLiterals(code);
  const bodies = typeBodies(skeleton);
  for (const match of code.matchAll(traceCall)) {
    const end = skeleton.indexOf(';', match.index);
    // The call as Trace.Name( and its arguments with whitespace collapsed.
    const text = `Trace.${match[1]}(` + code.slice(match.index + match[0].length, end < 0 ? undefined : end + 1)
      .replace(/\s+/g, ' ').replace(/^ /, '');
    const owner = bodies.filter(body => body.start < match.index && match.index < body.end)
      .sort((a, b) => b.start - a.start)[0]?.name;
    calls.push({ file, line: lineOf(match.index), text, owner });
  }
}

const inFallbackLogger = call => fallbackLoggers.some(logger => logger.file === call.file && logger.type === call.owner);
const entriesFor = call => traceAllowList.filter(entry => entry.file === call.file && call.text.includes(entry.fragment));
const direct = calls.filter(call => !inFallbackLogger(call));

test('the scan finds every fallback logger and the calls split across lines', () => {
  for (const logger of fallbackLoggers)
    expect(calls.some(call => call.file === logger.file && call.owner === logger.type), `${logger.file}: ${logger.type}`).toBe(true);
  expect(calls.some(call => call.file === 'packages/dotnet/Runic.Desktop/WebUiWindow.cs'
    && call.text.includes('BridgeHandshakeExpiredCode'))).toBe(true);
});

test('runtime code writes to Trace only through a fallback logger or the allow-list', () => {
  const unexpected = direct.filter(call => entriesFor(call).length === 0).map(call => `${call.file}:${call.line}: ${call.text}`);
  expect(unexpected, 'Log through ILogger with a [LoggerMessage] event, and fall back to the package\'s TraceFallbackLogger').toEqual([]);
  expect(imports, 'Call System.Diagnostics.Trace by name so the scan sees each call').toEqual([]);
});

test('every allow-list entry matches exactly one call', () => {
  const counts = traceAllowList.map(entry => ({ entry, matches: direct.filter(call => entriesFor(call).includes(entry)) }))
    .filter(({ matches }) => matches.length !== 1)
    .map(({ entry, matches }) => `${entry.file}: ${entry.fragment} matches ${matches.length} calls`);
  expect(counts).toEqual([]);
  const ambiguous = direct.filter(call => entriesFor(call).length > 1).map(call => `${call.file}:${call.line}`);
  expect(ambiguous).toEqual([]);
});
