import assert from "node:assert/strict";
import test from "node:test";

import {
  MvvmDevelopmentInspector,
  ProtocolTransport,
  createInjectedMvvmDevelopmentTools,
  encodeUtf8,
  reportMvvmInspectorToEndpoint,
  serializeJson,
} from "../dist/esm/index.js";

const ids = {
  session: "00000000-0000-4000-8000-000000000004",
  view: "00000000-0000-4000-8000-000000000002",
  request: "00000000-0000-4000-8000-000000000005",
  capability: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
};

class TestChannel {
  observer;
  sent = [];
  send(frame) {
    this.sent.push(frame.slice());
  }
  close() {}
  subscribe(observer) {
    this.observer = observer;
    return () => {
      this.observer = undefined;
    };
  }
  host(message) {
    this.observer?.frame(encodeUtf8(serializeJson(message)));
  }
}

test("inspector correlates bounded metadata without retaining payloads or capabilities", async () => {
  const times = [10, 22];
  const inspector = new MvvmDevelopmentInspector({
    capacity: 2,
    now: () => times.shift() ?? 22,
    members: [{
      id: 7,
      name: "step",
      sourceMember: "Example.CounterViewModel.Step",
      source: { file: "CounterViewModel.cs", line: 12, column: 6 },
    }],
  });
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);
  const stop = inspector.attach(transport);

  await transport.send({
    v: 1,
    kind: "setProperty",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    baseRevision: 4n,
    capability: ids.capability,
    payload: { member: 7, value: "private customer value" },
  });
  channel.host({
    v: 1,
    kind: "result",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    payload: { operation: "setProperty", revision: 5n },
  });

  assert.equal(inspector.events.length, 2);
  assert.deepEqual(
    inspector.events.map(({ direction, kind }) => ({ direction, kind })),
    [
      { direction: "client", kind: "setProperty" },
      { direction: "host", kind: "result" },
    ],
  );
  assert.equal(inspector.events[0].member, 7);
  assert.equal(inspector.events[0].memberName, "step");
  assert.equal(inspector.events[1].sourceMember, "Example.CounterViewModel.Step");
  assert.deepEqual(
    inspector.events[1].source,
    { file: "CounterViewModel.cs", line: 12, column: 6 },
  );
  assert.equal(inspector.events[1].revision, 5n);
  assert.equal(inspector.events[1].durationMilliseconds, 12);
  const retained = serializeJson(inspector.events);
  assert.doesNotMatch(retained, /private customer value/);
  assert.doesNotMatch(retained, new RegExp(ids.capability));

  transport.disconnect();
  assert.equal(inspector.events.length, 2);
  assert.equal(inspector.events[1].kind, "connection");
  stop();
});

test("endpoint reporter emits sanitized bigint-safe events", async () => {
  const requests = [];
  const inspector = new MvvmDevelopmentInspector();
  const stop = reportMvvmInspectorToEndpoint(
    inspector,
    "http://127.0.0.1:43123/events",
    {
      fetch: async (input, init) => {
        requests.push({ input: input.toString(), init });
        return new Response(null, { status: 204 });
      },
    },
  );
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);
  const detach = inspector.attach(transport);
  transport.disconnect();
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(requests.length, 1);
  assert.equal(requests[0].input, "http://127.0.0.1:43123/events");
  assert.match(requests[0].init.body, /"kind":"connection"/);
  assert.doesNotMatch(requests[0].init.body, /capability|payload|rawFrame/);
  detach();
  stop();
});

test("injected development tools accept only a loopback coordinator", () => {
  globalThis.__webuitoolkitMvvmDevelopment = {
    endpoint: "http://127.0.0.1:43123/token/events",
    projectDirectory: "/repo",
  };
  const development = createInjectedMvvmDevelopmentTools([]);
  assert.ok(development);
  assert.ok(development.inspector instanceof MvvmDevelopmentInspector);
  development.dispose();
  development.dispose();

  globalThis.__webuitoolkitMvvmDevelopment = {
    endpoint: "https://example.com/events",
  };
  assert.throws(() => createInjectedMvvmDevelopmentTools([]), /loopback HTTP/);
  delete globalThis.__webuitoolkitMvvmDevelopment;
});

test("inspector rejects unbounded retention", () => {
  assert.throws(() => new MvvmDevelopmentInspector({ capacity: 0 }), RangeError);
  assert.throws(() => new MvvmDevelopmentInspector({ capacity: 10_001 }), RangeError);
  assert.throws(
    () => new MvvmDevelopmentInspector({ members: [{ id: 0, name: "bad" }] }),
    RangeError,
  );
});
