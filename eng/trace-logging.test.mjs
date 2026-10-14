// Runtime messages go through ILogger when the application has an ILoggerFactory (#249). Direct
// System.Diagnostics.Trace output in packages/dotnet is reserved for the fallback loggers, which
// write to Trace only when there is no factory, and for the explicit allow-list below.
import { test, expect } from 'bun:test';
import { readFileSync, readdirSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

const root = resolve(import.meta.dir, '..');
const skippedDirectories = new Set(['bin', 'obj', 'node_modules', 'dist', '.cache']);
// Trace.TraceError(, Trace.WriteLine(, System.Diagnostics.Trace.Fail( and so on.
const traceCall = /\bTrace\s*\.\s*(Trace\w+|Write\w*|Fail|Assert)\s*\(/;

// Direct calls outside a fallback logger, by file and a fragment of the call's line. Each entry is the
// no-logger branch of a call that uses the logger first. Runic.Desktop has no fallback logger: without
// DesktopHostOptions.LoggerFactory these keep their earlier Trace text, which Runic.Desktop.Tests reads
// (BrowserHostTests.StalledLaunchListener), and the shutdown notice stays at Information.
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
    // The Bridge handshake warning; its message is on the next line.
    'Trace.TraceWarning(',
    'TraceWarning("A generated browser profile could not be deleted; it remains in the temporary folder.")',
    'TraceWarning($"The diagnostic sink failed for {BrowserLaunchStalledCode}',
    'TraceWarning($"{BrowserLaunchStalledCode}: {message}")',
  ].map(fragment => ({ file: 'packages/dotnet/Runic.Desktop/WebUiWindow.cs', fragment, exact: fragment === 'Trace.TraceWarning(' })),
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

// The class that declares `line`: the last class declaration before it. A fallback logger is a class
// that implements ILogger, such as TraceFallbackLogger.
function enclosingClass(lines, index) {
  for (let i = index; i >= 0; i--) {
    const match = lines[i].match(/\bclass\s+(\w+)[^{;]*/);
    if (match) return { name: match[1], logger: /:\s*([\w.]+\s*,\s*)*(Microsoft\.Extensions\.Logging\.)?ILogger\b/.test(match[0]) };
  }
  return null;
}

const calls = [];
const staticImports = [];
for (const path of sources(join(root, 'packages/dotnet'))) {
  const file = relative(root, path).replaceAll('\\', '/');
  const lines = readFileSync(path, 'utf8').split(/\r?\n/);
  lines.forEach((line, index) => {
    if (/^\s*using\s+static\s+System\.Diagnostics\.(Trace|Debug)\b/.test(line)) staticImports.push(`${file}:${index + 1}`);
    if (/^\s*\/\//.test(line) || !traceCall.test(line)) return;
    calls.push({ file, line: index + 1, text: line.trim(), owner: enclosingClass(lines, index) });
  });
}

const allowed = call => traceAllowList.find(entry => entry.file === call.file
  && (entry.exact ? call.text === entry.fragment : call.text.includes(entry.fragment)));

test('the scan finds the fallback loggers', () => {
  const fallbacks = new Set(calls.filter(call => call.owner?.logger).map(call => `${call.file}:${call.owner.name}`));
  for (const expected of [
    'packages/dotnet/Runic.Application.Views/RunicViewsTelemetry.cs:TraceFallbackLogger',
    'packages/dotnet/Runic.Navigation/NavigationTelemetry.cs:TraceFallbackLogger',
    'packages/dotnet/Runic.Application.Views.Wpf/WpfViewsLog.cs:TraceFallbackLogger',
  ]) expect(fallbacks).toContain(expected);
});

test('runtime code writes to Trace only through a fallback logger or the allow-list', () => {
  const unexpected = calls.filter(call => !call.owner?.logger && !allowed(call))
    .map(call => `${call.file}:${call.line}: ${call.text}`);
  expect(unexpected, 'Log through ILogger with a [LoggerMessage] event, and fall back to the package\'s TraceFallbackLogger').toEqual([]);
  expect(staticImports).toEqual([]);
});

test('every allow-list entry still matches a call', () => {
  const stale = traceAllowList.filter(entry => !calls.some(call => !call.owner?.logger && allowed(call) === entry))
    .map(entry => `${entry.file}: ${entry.fragment}`);
  expect(stale).toEqual([]);
});
