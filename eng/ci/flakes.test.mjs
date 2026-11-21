import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { root } from '../run.mjs';
import { collect, failingTests, firstError, format, messageHead, normalizeError, platforms, report, sinceDate, stepLog, withSmallerPages } from './flakes.mjs';

// Recorded, trimmed gh api responses of runic-sdk's ci.yml runs on 2026-11-16 and 17:
// - the VoiceOver return (#1093, closed) and PathWatcher (#1008, reopened) flakes twice each, both passing on a rerun;
// - a Windows test session that hit its --timeout;
// - a merge queue entry whose two jobs failed the same bun test;
// - the Inspect React page failure that #1054 names only by its error;
// - three template jobs whose logs name no test;
// - a passed first attempt and a cancelled run.
const fixtures = resolve(root, 'eng/ci/fixtures/flakes');
const read = name => readFileSync(resolve(fixtures, name), 'utf8');
const recorded = (overrides = {}) => {
  const requests = [];
  return {
    requests,
    json: async path => {
      requests.push(path);
      if (path.includes('/workflows/ci.yml/runs?')) return JSON.parse(read('runs.json')).workflow_runs;
      const run = /\/actions\/runs\/(\d+)\/jobs\?filter=all/.exec(path);
      if (run) return JSON.parse(read(`jobs-${run[1]}.json`)).jobs;
      if (/\/issues\?state=all/.test(path)) return overrides.issues ?? JSON.parse(read('issues.json'));
      throw new Error(`unrecorded request ${path}`);
    },
    log: async id => {
      requests.push(`log ${id}`);
      if (!existsSync(resolve(fixtures, `log-${id}.txt`))) throw new Error(`no log for ${id}`);
      return read(`log-${id}.txt`);
    },
  };
};
const since = new Date('2026-11-15T16:00:00Z');
const entry = (result, name) => {
  const found = result.groups.filter(group => group.name === name);
  assert.equal(found.length, 1, `${name}: ${found.length} entries`);
  return found[0];
};
const voiceOver = 'voiceover-side-panel-return';
const pathWatcher = 'DirectoryReplacedWithoutAReadIsWatchedAgain';
const pressed = 'Runic Inspect keeps a pressed card link through a refresh in chromium';
const reactPage = 'Runic Inspect on a React page in webkit';

test('failure lines name the failing tests of TUnit, bun, node:test and the probes, each with its first error line', () => {
  const log = [
    '2026-11-17T12:43:33.7829665Z failed DirectoryReplacedWithoutAReadIsWatchedAgain(False) (793ms)',
    '  TUnit.Engine.Exceptions.TestFailedException: [Test Failure] InvalidOperationException: The watcher polls. events=5',
    '    at Runic.Application.Testing.Tests.PathWatcherTests.Require(Boolean condition) in /_/tests/PathWatcherTests.cs:722',
    '2026-11-17T12:43:33.8Z failed DirectoryReplacedWithoutAReadIsWatchedAgain(True) (1s 2ms)',
    '  TUnit.Engine.Exceptions.TestFailedException: [Test Failure] InvalidOperationException: The watcher polls. events=7',
    '(pass) Runic Inspect on a React page in chromium [1521.99ms]',
    '37 |     if (Date.now() > deadline) throw new Error(`No editor was launched`);',
    'error: No editor was launched: /tmp/runic-inspect-browser-5jnA3t/editor-arguments.txt holds no arguments.',
    '      at readRecord (/home/runner/work/runic-sdk/runic-sdk/tests/support/recording-editor.mjs:37:42)',
    '##[error]',
    '\x1b[31m(fail) Runic Inspect on a React page in webkit [41181.91ms]\x1b[0m',
    '✖ writes a lock file (12.5ms)',
    '  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:',
    'FAIL           voiceover-side-panel-return: VoiceOver did not say \'Finish\'; it said: You are currently on a button.',
    '[44.80s] FAIL window-restart: after the Window restarted: NOT stopped',
    'running... 3 completed, 0 failed | active: SomethingElse',
    '  failed: 0',
  ].join('\n');
  assert.deepEqual(failingTests(log).map(({ name, error }) => [name, error]), [
    // Both cases of the parameterized test are one test, and their counts read the same.
    ['DirectoryReplacedWithoutAReadIsWatchedAgain', 'InvalidOperationException: The watcher polls. events=N'],
    ['Runic Inspect on a React page in webkit', 'error: No editor was launched: <path> holds no arguments.'],
    ['writes a lock file', 'AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:'],
    // A probe's message is its signature up to the first "; ": what VoiceOver said instead varies.
    ['voiceover-side-panel-return', 'VoiceOver did not say \'Finish\''],
    ['window-restart', 'after the Window restarted: NOT stopped'],
  ]);
  // A session stopped by its timeout names the test then active; a passing log names none.
  assert.deepEqual(failingTests(read('log-134074693218.txt')).map(item => item.name), ['DevelopmentOpensALoopbackEndpointInAnInstalledBrowser']);
  assert.deepEqual(failingTests('running... 9 completed, 0 failed | active: Waits\nTest run summary: Passed!'), []);
});

test('errors normalize to a signature, and their words are what an issue quotes', () => {
  assert.equal(normalizeError('error: No editor was launched within 40000 ms: C:\\a\\x\\y.txt and /tmp/a-1/b holds {"a":1}'),
    'error: No editor was launched within N ms: <path> and <path> holds {…}');
  assert.equal(messageHead('error: No editor was launched: /tmp/x/editor-arguments.txt holds no arguments.'), 'No editor was launched');
  assert.equal(messageHead('TUnit.Engine.Exceptions.TestFailedException: [Test Failure] InvalidOperationException: The watcher polls instead of watching. disposed=False'),
    'The watcher polls instead of watching. disposed');
  assert.equal(messageHead('error: bash exited with 1'), null, 'too short to tell failures apart');
});

test('a step whose log names no test takes its first meaningful ##[error] line, else the error printed before it', () => {
  assert.equal(firstError('##[error]Process completed with exit code 1.\n##[error]win-arm64 lacks its library.'), 'win-arm64 lacks its library.');
  // A wrapper ("bash exited with 1") is the signature only when nothing else was printed.
  assert.equal(firstError('TypeError: x is undefined\nerror: bash exited with 1\n##[error]\n##[error]Process completed with exit code 1.'), 'TypeError: x is undefined');
  assert.equal(firstError('  33 |   throw new Error(`${command} exited`);\nerror: bash exited with 1\n##[error]'), 'error: bash exited with 1');
  assert.equal(firstError('##[error]Process completed with exit code 2.'), 'Process completed with exit code 2.');
  // The template lane's lock failure (sdk#1128's train): the build error, not verify-templates.mjs's "bash exited with 1".
  assert.equal(firstError(read('log-133503092706.txt')).slice(0, 62), 'error RUNICBRIDGE016: The frontend install \'pnpm install --fro');
});

// Failure texts from runic-sdk ci.yml job logs of 2026-10-22 to 2026-11-18 (M2's 31 days), trimmed.
test('a step error is the cause the log printed, not an echo, a wrapper or a follow-up error', () => {
  const at = text => text.split('\n').map((line, index) => `2026-11-12T10:00:${String(index).padStart(2, '0')}.1234567Z ${line}`).join('\n');
  // Windows message boxes (job 128362699302): a passing case's captured stderr is echoed; the step then exited 127.
  const boxes = at(['StandardError: child-error ', '[32mpassed[m ElevatedChildAsync [90m(1s 12ms)[m', '[90m  Standard output',
    '    StandardError: child-error ', 'PASS an unowned file dialog shows and closes', '##[error]Process completed with exit code 127.'].join('\n'));
  assert.equal(firstError(boxes), 'Process completed with exit code 127.');
  assert.equal(normalizeError(firstError(boxes)), 'Process completed with exit code 127.', 'an exit code stays');
  // NativeAOT publishes (job 124016375177): ILC's warning-as-error is the cause, MSB3077 follows from it.
  assert.equal(normalizeError(firstError(at([
    '  Generating native code',
    "ILC : error IL2037: Runic.Desktop.Internal.PresentationServers.CreateKestrel(Action`1<IServiceCollection>): No members were resolved for 'NonPublicConstructors' on type 'Runic.Desktop.Kestrel.KestrelPresentationServerFactory'. [C:\\a\\runic-sdk\\Runic.Platform.Runtime.Tests.csproj]",
    'C:\\a\\runic-sdk\\.cache\\nuget\\microsoft.dotnet.ilcompiler\\10.0.14\\build\\Microsoft.NETCore.Native.targets(330,5): error MSB3077: The command "ilc" exited with return value 0, but errors were detected during execution.',
    '##[error]Process completed with exit code 1.'].join('\n')))).slice(0, 51), 'error IL2037: Runic.Desktop.Internal.PresentationSe');
  // A NuGet error keeps its code: NU1403 is a content hash, not a network failure.
  assert.equal(normalizeError(firstError(at(['/home/runner/work/runic-sdk/tools/dotnet-runic/Runic.Application.Tool.csproj : error NU1403: Package content hash validation failed for Runic.CommandLine.0.12.0. The package is different than the last restore.',
    '    1 Error(s)', '##[error]Process completed with exit code 1.'].join('\n')))).slice(0, 60), 'error NU1403: Package content hash validation failed for Run');
  // The macOS disk image (job 121203034793, #1018): hdiutil's line; an issue quotes its words after the tool's name.
  const hdiutil = firstError(at(['artifacts/package/macos/Runic Packaging Probe.app/Contents/Info.plist: OK',
    'hdiutil: attach failed - Resource temporarily unavailable', '##[error]Process completed with exit code 1.'].join('\n')));
  assert.equal(hdiutil, 'hdiutil: attach failed - Resource temporarily unavailable');
  assert.equal(messageHead(hdiutil), 'attach failed - Resource temporarily unavailable');
  // A native smoke's deadline (job 123073889139) and an unhandled exception (job 131359875705) after earlier output.
  assert.equal(firstError(at(['PASS A frameless window that cannot be resized becomes the key window.', 'WINDOW CHROME DONE',
    'Native window smoke exceeded its 90-second deadline; see the last logged phase.', '##[error]Process completed with exit code 1.'].join('\n'))),
  'Native window smoke exceeded its 90-second deadline; see the last logged phase.');
  assert.equal(normalizeError(firstError(at(['Unhandled exception. System.ComponentModel.Win32Exception (1326): The user runic-hosted could not log on.',
    '   at Unelevated.UserEnvironment(String user, String password) in /_/tests/native/Runic.Application.HostedWindows.Smoke/Unelevated.cs:line 50',
    '##[error]Process completed with exit code 127.'].join('\n')))), 'Unhandled exception. System.ComponentModel.Win32Exception (N): The user runic-hosted could not log on.');
  // PowerShell's thrown message (job 121373708633, the WPF UI Automation check).
  assert.equal(firstError(at(["PASS shell window - WPF/Window 'Hybrid notes' class=Window", 'UI Automation element was not available: the web editor document',
    'At D:\\a\\runic-sdk\\runic-sdk\\tests\\native\\wpf-ui-automation\\wpf-ui-automation.ps1:92 char:5', '+     throw "UI Automation element was not available: $What"',
    '    + FullyQualifiedErrorId : UI Automation element was not available: the web editor document', '##[error]Process completed with exit code 1.'].join('\n'))),
  'UI Automation element was not available: the web editor document');
  // apt-install.sh out of attempts (job 119981188076): the last attempt names the command.
  assert.match(firstError(at(['##[warning]Timed out after 600s: sudo DEBIAN_FRONTEND=noninteractive apt-get install --yes libwebkit2gtk-4.1-0',
    '##[warning]Attempt 2 of 3 failed with status 143: sudo DEBIAN_FRONTEND=noninteractive apt-get install --yes libwebkit2gtk-4.1-0',
    '##[warning]Attempt 3 of 3 failed with status 143: sudo DEBIAN_FRONTEND=noninteractive apt-get install --yes libwebkit2gtk-4.1-0',
    '##[error]Process completed with exit code 143.'].join('\n'))), /^##\[warning\]Attempt 3 of 3 failed with status 143: sudo/);
});

test('a job log is cut to the lines its failed step wrote', () => {
  const log = ['2026-11-12T10:00:00.5Z ##[group]Run quarantined tests', '2026-11-12T10:00:01.2Z failed SomeQuarantinedTest (2s 1ms)',
    '2026-11-12T10:00:01.3Z ##[error]Process completed with exit code 1.', '2026-11-12T10:00:02.0Z ##[group]Run hdiutil attach',
    '2026-11-12T10:00:03.9Z hdiutil: attach failed - Resource temporarily unavailable', '2026-11-12T10:00:04.1Z ##[error]Process completed with exit code 1.',
    '2026-11-12T10:00:05.0Z Post job cleanup.'].join('\n');
  const step = stepLog(log, { name: 'Package and open the .app', started_at: '2026-11-12T10:00:02Z', completed_at: '2026-11-12T10:00:04Z' });
  assert.deepEqual(failingTests(step), [], 'the test failed in an earlier step that continued on error');
  assert.equal(firstError(step), 'hdiutil: attach failed - Resource temporarily unavailable');
  // Without times, or without timestamped lines in the window, the whole log is read.
  assert.equal(stepLog(log, { name: 'x' }), log);
  assert.equal(stepLog(log, undefined), log);
  assert.equal(stepLog(log, { started_at: '2026-11-13T00:00:00Z', completed_at: '2026-11-13T00:00:01Z' }), log);
});

test('a server error retries the page with half the page size, down to 25', async () => {
  const asked = [];
  const get = async path => {
    asked.push(path);
    if (!path.includes('per_page=25')) throw new Error('gh api repos/r/actions/workflows/ci.yml/runs?per_page=100: HTTP 502: Bad Gateway');
    return ['ok'];
  };
  assert.deepEqual(await withSmallerPages(get, 'repos/r/runs?created=x&per_page=100'), ['ok']);
  assert.deepEqual(asked, ['repos/r/runs?created=x&per_page=100', 'repos/r/runs?created=x&per_page=50', 'repos/r/runs?created=x&per_page=25']);
  await assert.rejects(withSmallerPages(async () => { throw new Error('HTTP 404: Not Found'); }, 'a?per_page=100'), /404/);
  await assert.rejects(withSmallerPages(async () => { throw new Error('HTTP 502'); }, 'a?per_page=25'), /502/);
});

test('--since takes days, hours or a date', () => {
  const now = new Date('2026-11-17T12:00:00Z');
  assert.equal(sinceDate('2d', now).toISOString(), '2026-11-15T12:00:00.000Z');
  assert.equal(sinceDate('36h', now).toISOString(), '2026-11-16T00:00:00.000Z');
  assert.equal(sinceDate('2026-11-10', now).toISOString(), '2026-11-10T00:00:00.000Z');
  assert.throws(() => sinceDate('yesterday', now), /--since/);
});

test('the report groups recorded failures by signature across runs, jobs and legs, and reads only failed or rerun runs', async () => {
  const source = recorded();
  const data = await collect(source, { since });
  // Only reads: the run list, failed or rerun runs' jobs (every attempt), failed jobs' logs (not verify's) and issues.
  assert.ok(source.requests.every(request => /^(log \d+|repos\/Runic-Artifex\/runic-sdk\/(actions\/|issues\?))/.test(request)), source.requests.join('\n'));
  assert.equal(data.runs, 8, 'the passed first attempt and the cancelled run are not read');
  assert.equal(data.cancelled, 1);
  assert.ok(!data.failures.some(failure => failure.job === 'verify'));
  const result = report(data);
  assert.equal(result.cancelled, 1);
  // The same bun failure in the Node 24 and Node 26 jobs is one entry; VoiceOver's two different replies are one too.
  assert.deepEqual(entry(result, pressed).jobs.sort(), ['Web packages and framework consumers', 'npm packages / Node 26']);
  assert.equal(entry(result, pressed).runs, 1);
  assert.equal(entry(result, voiceOver).runs, 2);
  // #1093 closed after both sightings; #1008 was reopened, so the PathWatcher failure is a known flake.
  assert.equal(entry(result, voiceOver).status, 'fixed');
  assert.equal(entry(result, pathWatcher).status, 'known');
  assert.deepEqual(entry(result, pathWatcher).issues.map(issue => issue.number), [1008], 'pull requests are not issues');
  // #1054 names the test only as exerciseMessages; it quotes the error.
  assert.deepEqual(entry(result, reactPage).issues.map(issue => issue.number), [1054]);
  assert.equal(entry(result, reactPage).status, 'fixed');
  // A job that passed on a rerun of the same commit is a flake; one that failed without a rerun is not known to be.
  assert.equal(entry(result, pathWatcher).flake, true);
  assert.equal(entry(result, voiceOver).flake, true);
  assert.equal(entry(result, 'DevelopmentOpensALoopbackEndpointInAnInstalledBrowser').flake, true);
  assert.equal(entry(result, pressed).flake, false);
  assert.deepEqual(entry(result, pathWatcher).sightings.map(item => [item.event, item.attempt, item.passedOnRerun]),
    [['push', 1, false], ['pull_request', 1, true]]);
  // The template jobs' logs name no test: each step is an entry with its error, one run each.
  const react = entry(result, 'step: Verify generated react template applications');
  assert.deepEqual([react.test, react.error.slice(0, 42), react.runs, react.jobs.length], [false, 'error RUNICBRIDGE016: The frontend install', 1, 2]);
  // The first queue entry failed in the VoiceOver flake while #1093 was still open: re-queue. The second failed in a test
  // that no issue names and that failed for no other pull request: it may be the change.
  assert.deepEqual(result.queue.map(entry => [entry.run, entry.verdict]), [[43909958494, 're-queue'], [43878333721, 'investigate']]);
  assert.match(format(result), /Failed merge queue entries \(re-queue 1, file-issue 0, investigate 1\):\n {2}re-queue {4}pr-1073[^\n]*\n {2}investigate pr-1052[^\n]*\n {4}first-sighting: Runic Inspect keeps a pressed card link/);
  const text = format(result);
  assert.match(text, /; 1 cancelled \(not classified\)/);
  assert.match(text, new RegExp(`  ${pathWatcher}: 2 runs, Native / win-arm64 — #1008 \\(open\\) — flake: passed on a rerun of the same commit\\n    InvalidOperationException: The watcher polls`));
  assert.match(text, /\(passed on rerun\)/);
});

test('an open issue makes a known flake, and a queue entry failing only in known flakes is re-queued', async () => {
  const issues = JSON.parse(read('issues.json')).map(issue => issue.number === 1093 ? { ...issue, state: 'open', closed_at: null } : issue);
  const result = report(await collect(recorded({ issues }), { since }));
  assert.equal(entry(result, voiceOver).status, 'known');
  assert.equal(result.queue.find(item => item.run === 43909958494).verdict, 're-queue');
  assert.match(format(result), /Known flakes[^\n]*:\n(?:.*\n)*? {2}voiceover-side-panel-return: 2 runs, Native \/ osx-arm64 — #1093 \(open\)/);
});

test('an entry seen twice without an issue needs one; after its issue closed, it regressed; a step-only repeat is read', async () => {
  const none = report(await collect(recorded({ issues: [] }), { since }));
  assert.equal(entry(none, voiceOver).status, 'needs-issue');
  assert.match(format(none), /no issue mentions it[^\n]*:\n(?:.*\n)*? {2}voiceover-side-panel-return: 2 runs/);
  const early = JSON.parse(read('issues.json')).map(issue => ({ ...issue, state: 'closed', closed_at: '2026-11-16T12:00:00Z' }));
  assert.equal(entry(report(await collect(recorded({ issues: early }), { since })), voiceOver).status, 'regressed');
  // With --min 3 two sightings are not yet a pattern.
  assert.equal(entry(report(await collect(recorded({ issues: [] }), { since }), { min: 3 }), voiceOver).status, 'first-sighting');
  // A step-only failure in two runs with the same error is one repeated entry; another error is another entry.
  const failure = (run, error) => ({ name: 'step: Verify generated react template applications', test: false, error, raw: error,
    job: 'Templates / react+svelte', step: 'Verify generated react template applications', run, attempt: 1, passedOnRerun: false,
    event: 'pull_request', branch: 'b', pullRequests: [1], url: `https://example.invalid/runs/${run}/job/1`, at: '2026-11-16T00:00:00Z' });
  const steps = report({ repository: 'r', since: since.toISOString(), runs: 3, issues: [],
    failures: [failure(1, 'error: bash exited with N'), failure(2, 'error: bash exited with N'), failure(3, 'TypeError: x')] });
  assert.deepEqual(steps.groups.map(group => [group.error, group.runs, group.status]),
    [['error: bash exited with N', 2, 'repeated-step'], ['TypeError: x', 1, 'first-sighting']]);
});

test('queue verdicts: infrastructure and recently closed issues re-queue, unfiled flakes need an issue first', () => {
  const failure = (run, { name, error, test = true, event = 'merge_group', pr = run, at = '2026-11-18T00:00:00Z', passedOnRerun = false }) => ({
    name, error: normalizeError(error), raw: error, test, job: 'Native / win-x64', step: 'Run the tests', run, attempt: 1, passedOnRerun, event,
    branch: event === 'merge_group' ? `gh-readonly-queue/main/pr-${pr}-abc` : `branch-${pr}`, pullRequests: event === 'merge_group' ? [] : [pr],
    url: `https://example.invalid/actions/runs/${run}/job/${run}`, at });
  // M2's infrastructure failures, from the 31 days' logs.
  const setup = { name: 'step: Run ./.github/actions/setup-sdk', test: false, error: 'Failed to install dotnet, exit code: 1. Fatal error.' };
  const artifact = { name: 'step: Run ./.github/actions/restore-build', test: false, error: 'Unable to download artifact(s): Unable to download and extract artifact: Artifact download failed after 5 retries.' };
  const apt = { name: 'step: Install Orca and the native WebView prerequisites', test: false,
    error: 'libwebkit2gtk-4.1-0 libwebkitgtk-6.0-4 xvfb dbus-x11 at-spi2-core orca could not be downloaded. attempt 2 via us-east-2.ec2.archive.ubuntu.com' };
  const nvda = { name: 'nvda-announcement-assertive', error: "NVDA did not say 'Runic assertive announcement'; it said: Runic" };
  const bookkeeping = { name: 'HandWrittenCallerFailureBookkeepingAsync', error: 'InvalidOperationException: Event 3 for the native command differs: context=/a logged=True' };
  const broken = { name: 'ANewTestThatFails', error: 'InvalidOperationException: The new behaviour is wrong.' };
  const issues = [{ number: 988, state: 'closed', closedAt: '2026-11-15T00:00:00Z', title: 'Flaky: nvda-announcement-assertive', text: 'nvda-announcement-assertive fails on Windows', url: 'u' }];
  const result = report({ repository: 'r', since: '2026-11-01T00:00:00Z', runs: 9, issues, failures: [
    failure(1, setup), failure(2, artifact), failure(3, apt),
    // #988 closed three days before this failure: a regression of a known flake.
    failure(4, nvda),
    // Seen for two pull requests: a flake that needs its issue.
    failure(5, bookkeeping), failure(6, { ...bookkeeping, event: 'pull_request', pr: 70 }),
    // Only this change fails it.
    failure(7, broken), failure(8, { ...broken, event: 'pull_request', pr: 7 }),
    // #988 closed ten days before: not recent any more, but a flake by evidence (two pull requests): reopen it.
    failure(9, { ...nvda, at: '2026-11-25T00:00:00Z' }),
  ] });
  const status = name => result.groups.find(group => group.name === name).status;
  assert.deepEqual([setup, artifact, apt].map(item => status(item.name)), ['infrastructure', 'infrastructure', 'infrastructure']);
  assert.equal(status(nvda.name), 'regressed', 'listed under "failed again after its issue closed"');
  assert.equal(status(bookkeeping.name), 'needs-issue');
  assert.deepEqual(result.queue.map(entry => [entry.run, entry.verdict]), [[1, 're-queue'], [2, 're-queue'], [3, 're-queue'], [4, 're-queue'],
    [5, 'file-issue'], [7, 'investigate'], [9, 'file-issue']]);
  const text = format(result);
  assert.match(text, /Infrastructure: [^\n]*:\n {2}step: Install Orca/);
  assert.match(text, /Failed again after its issue closed[^\n]*:\n {2}nvda-announcement-assertive: 2 runs, Native \/ win-x64 — #988 \(closed\)/);
  assert.match(text, /\(re-queue 4, file-issue 2, investigate 1\)/);
  // A named test's failure is the test's, whatever its message says.
  const named = report({ repository: 'r', since: '2026-11-01T00:00:00Z', runs: 1, issues: [], failures: [failure(1, { name: 'ConnectsThroughTheProxy', error: 'SocketException: ETIMEDOUT' })] });
  assert.deepEqual([named.groups[0].status, named.queue[0].verdict], ['first-sighting', 'investigate']);
});

test('an issue covers an entry by citing its run, quoting its error or naming its test on its platform; the test name alone is weak', () => {
  const failure = (run, { name, error, test = true, job = 'Native / linux-x64' }) => ({ name, error: normalizeError(error), raw: error, test, job,
    step: 'Run the tests', run, attempt: 1, passedOnRerun: false, event: 'merge_group', branch: `gh-readonly-queue/main/pr-${run}-abc`,
    pullRequests: [], url: `https://github.com/Runic-Artifex/runic-sdk/actions/runs/${run}/job/${run}1`, at: '2026-11-20T10:00:00Z' });
  // 2026-11-20: #35, a Windows connection timeout, named the Chromium host test; the linux-x64 failure was another one (#1273).
  const chromium = { name: 'OwnsChromiumProcessProfileRestartAndNaturalExit',
    error: 'InvalidOperationException: Generated profile remains at /tmp/runic-chromium-x1; the directory may have been recreated.' };
  // The GTK 4 step's InspectorEnvironmentSmoke port collisions, which #1257 cites by run and #1258 fixed; the log names no test.
  const gtk = { name: 'step: Exercise GTK4 native window and platform services', test: false, error: 'Process completed with exit code 1.' };
  const issues = [
    { number: 35, state: 'closed', closedAt: '2026-10-30T00:00:00Z', title: 'Flaky: OwnsChromiumProcessProfileRestartAndNaturalExit',
      text: 'OwnsChromiumProcessProfileRestartAndNaturalExit timed out connecting on win-x64 (Windows).', url: 'u35' },
    { number: 1257, state: 'closed', closedAt: '2026-11-20T12:00:00Z', title: 'InspectorEnvironmentSmoke: port in use',
      text: 'The GTK 4 step failed in https://github.com/Runic-Artifex/runic-sdk/actions/runs/44712377949/job/1 and runs/4471237794 (another).', url: 'u1257' },
  ];
  const result = report({ repository: 'r', since: '2026-11-19T00:00:00Z', runs: 3, issues,
    failures: [failure(44712377949, gtk), failure(44700000001, chromium), failure(44700000002, chromium)] });
  const group = name => result.groups.find(item => item.name === name);
  assert.deepEqual([group(chromium.name).status, group(chromium.name).issues, group(chromium.name).weakIssues.map(issue => issue.number)], ['needs-issue', [], [35]]);
  assert.deepEqual([group(gtk.name).status, group(gtk.name).issues.map(issue => [issue.number, issue.how])], ['fixed', [[1257, 'cites a run']]]);
  // The issue closed after the failure: it was open then, so that queue entry is re-queued.
  assert.equal(result.queue.find(entry => entry.run === 44712377949).verdict, 're-queue');
  assert.match(format(result), /OwnsChromiumProcessProfileRestartAndNaturalExit: 2 runs, Native \/ linux-x64 — names the test only \(weak\): #35 \(closed\)/);
  // On its own platform, the test name is enough.
  const windows = report({ repository: 'r', since: '2026-11-19T00:00:00Z', runs: 1, issues,
    failures: [failure(1, { ...chromium, job: 'Native / win-x64' })] });
  assert.deepEqual(windows.groups[0].issues.map(issue => [issue.number, issue.how]), [[35, 'names the test on its platform']]);
  assert.deepEqual(platforms('Screen reader / nvda'), ['nvda', 'Windows']);
  assert.deepEqual(platforms('Native / osx-arm64'), ['osx-arm64', 'macOS', 'osx']);
  assert.deepEqual(platforms('Managed tests'), []);
});
