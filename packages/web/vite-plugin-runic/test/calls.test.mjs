// The dock's Bridge call timeline (#954): what the development client may send for a call, how the server names the
// call's C# handler from the development source map, and what stays out of /__runic/state.
import assert from "node:assert/strict";
import test from "node:test";
import { callEntry, sanitizeCallReport, stackFrames } from "../dist/calls.js";
import { parseSourceMap } from "../dist/sources.js";
import { createDevelopmentTimeline, initialDevelopmentState, withoutFailures } from "../dist/timeline.js";

const map = parseSourceMap({
  format: "runic-bridge-sources/1",
  viewModels: [{
    id: "Demo.EditorViewModel", name: "Editor", route: "editor", contract: "Demo.EditorViewModel:AA", pageKinds: ["editor"],
    symbol: "Demo.EditorViewModel", location: { file: "/src/EditorViewModel.cs", line: 5, column: 29 },
  }],
  members: [
    {
      id: "Demo.EditorViewModel/command/save", viewModel: "Demo.EditorViewModel", member: "save", kind: "command",
      symbol: "Demo.EditorViewModel.SaveAsync()", property: "Demo.EditorViewModel.SaveCommand",
      location: { file: "/src/EditorViewModel.cs", line: 12, column: 5 },
    },
    {
      id: "Demo.EditorViewModel/commandState/canSave", viewModel: "Demo.EditorViewModel", member: "canSave", kind: "commandState",
      symbol: "Demo.EditorViewModel.SaveAsync()", location: { file: "/src/EditorViewModel.cs", line: 12, column: 5 },
    },
    {
      id: "Demo.EditorViewModel/state/title", viewModel: "Demo.EditorViewModel", member: "title", kind: "state",
      symbol: "Demo.EditorViewModel.Title", location: { file: "/src/EditorViewModel.cs", line: 8, column: 19 },
    },
    {
      id: "Demo.EditorViewModel/setter/title", viewModel: "Demo.EditorViewModel", member: "title", kind: "setter",
      symbol: "Demo.EditorViewModel.Title.set", location: { file: "/src/EditorViewModel.cs", line: 8, column: 40 },
    },
  ],
});

const save = { kind: "command", route: "editor", contract: "Demo.EditorViewModel:AA", member: "save", duration: 23.04, outcome: "ok", revision: 4 };

test("a call names its C# handler from the source map", () => {
  const report = sanitizeCallReport(save);
  const entry = callEntry(report, map, "call-1", "2026-11-15T00:00:00.000Z");
  assert.equal(entry.label, "command EditorViewModel.save → SaveAsync() 23 ms");
  assert.deepEqual(entry.handler, {
    symbol: "Demo.EditorViewModel.SaveAsync()", method: "SaveAsync()", source: "Demo.EditorViewModel/command/save",
    fileName: "EditorViewModel.cs", line: 12,
  });
  assert.equal(entry.duration, 23);
  assert.equal(entry.revision, 4);
  // A setter call runs the setter, not the state getter of the same member.
  const set = callEntry(sanitizeCallReport({ ...save, kind: "set", member: "title", duration: 1.26 }), map, "call-2", "t");
  assert.equal(set.label, "set EditorViewModel.title → Title.set 1.3 ms");
  // An older build's fingerprint still names the type's members.
  assert.equal(callEntry(sanitizeCallReport({ ...save, contract: "Demo.EditorViewModel:BB" }), map, "call-3", "t").handler?.method, "SaveAsync()");
  // Without a map, or for a member it does not list, there is no handler.
  assert.equal(callEntry(sanitizeCallReport(save), undefined, "call-4", "t").label, "command EditorViewModel.save 23 ms");
  assert.equal(callEntry(sanitizeCallReport({ ...save, member: "close" }), map, "call-5", "t").handler, undefined);
  // The outcome shows when it is not ok.
  assert.equal(callEntry(sanitizeCallReport({ ...save, outcome: "cancelled", reason: "superseded" }), map, "call-6", "t").label,
    "command EditorViewModel.save → SaveAsync() 23 ms · cancelled (superseded)");
});

test("the handler's own duration shows when the reply carried it, and .NET's name when the map has none", () => {
  const timed = callEntry(sanitizeCallReport({ ...save, handlerDuration: 21.44, handlerName: "EditorViewModel.SaveAsync" }), map, "call-1", "t");
  assert.equal(timed.handlerDuration, 21.4);
  assert.equal(timed.handlerName, undefined, "the map's handler wins");
  const unmapped = callEntry(sanitizeCallReport({ ...save, handlerDuration: 2, handlerName: "EditorViewModel.SaveAsync" }), undefined, "call-2", "t");
  assert.equal(unmapped.label, "command EditorViewModel.save → EditorViewModel.SaveAsync 23 ms");
  assert.equal(unmapped.handlerDuration, 2);
});

test("a call report has a closed shape: names, finite durations, and a redacted failure only for an error", () => {
  for (const refused of [
    null, [], "call", { ...save, kind: "" }, { ...save, route: "a/b" }, { ...save, member: "x y" }, { ...save, contract: "/etc/passwd" },
    { ...save, duration: -1 }, { ...save, duration: Number.NaN }, { ...save, duration: 1e12 }, { ...save, outcome: "maybe" },
  ]) {
    assert.equal(sanitizeCallReport(refused), undefined, JSON.stringify(refused));
  }
  const report = sanitizeCallReport({
    ...save, reason: "token=abc", revision: -2, handlerName: "Bearer abc", page: "../x", extra: "dropped",
    failure: { type: "System.Exception", message: "no" },
  });
  assert.deepEqual(report, { kind: "command", route: "editor", contract: "Demo.EditorViewModel:AA", member: "save", duration: 23, outcome: "ok" });
  const failed = sanitizeCallReport({
    ...save, outcome: "error", reason: "failed",
    failure: { type: "System.InvalidOperationException", message: "Login failed: password=hunter2",
      stack: "   at Demo.EditorViewModel.SaveAsync() in /src/EditorViewModel.cs:line 14" },
  });
  assert.equal(failed.failure.message, "Login failed: password=[redacted]");
  assert.match(failed.failure.stack, /EditorViewModel\.cs:line 14/u, "a failure keeps its file paths");
});

test("the call history is bounded, and failures stay out of the state endpoint", () => {
  const timeline = createDevelopmentTimeline(initialDevelopmentState(undefined), 3, () => {});
  for (let index = 0; index < 5; index += 1) timeline.appendCall({ ...save, revision: index }, map);
  assert.deepEqual(timeline.state.calls.map((call) => [call.id, call.revision]), [["call-3", 2], ["call-4", 3], ["call-5", 4]]);
  assert.equal(timeline.appendCall({ kind: "command" }, map), undefined);
  timeline.appendCall({ ...save, outcome: "error", failure: { type: "System.Exception", message: "Disk full.", stack: "at /src/A.cs" } }, map);
  assert.equal(timeline.state.calls.at(-1).failure.type, "System.Exception");
  const published = JSON.stringify(withoutFailures(timeline.state));
  assert.doesNotMatch(published, /Disk full|\/src\//u);
  assert.match(published, /EditorViewModel\.cs/u, "the handler's file name, not its path, is in the state");
});

test("the .NET frames with a source location are the stack's links", () => {
  const frames = stackFrames([
    "System.InvalidOperationException: Disk full.",
    "   at Demo.Storage.Write(String path) in /src/Storage.cs:line 30",
    "   at Demo.EditorViewModel.SaveAsync() in C:\\src\\EditorViewModel.cs:line 14",
    "   at System.Threading.Tasks.Task.Run()",
  ].join("\n"));
  assert.deepEqual(frames.map((frame) => [frame.method, frame.file, frame.line]), [
    ["Demo.Storage.Write(String path)", "/src/Storage.cs", 30],
    ["Demo.EditorViewModel.SaveAsync()", "C:\\src\\EditorViewModel.cs", 14],
  ]);
  assert.deepEqual(stackFrames(undefined), []);
});
