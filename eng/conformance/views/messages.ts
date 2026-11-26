// Writing and comparing scenario messages under specs/application/conformance/README.md, "Comparing messages" and
// "Identifiers". Shared by every implementation: nothing here knows which language answers.
export class ScenarioFailure extends Error {}

export type Id = { kind: string; value?: string };

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);

export function typeOf(value: unknown): string {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  return typeof value === "object" ? "object" : typeof value; // string, number, boolean
}

export function abbreviate(value: unknown, max = 600): string {
  const text = typeof value === "string" ? value : JSON.stringify(value);
  return text.length <= max ? text : `${text.slice(0, max)}… (${text.length} chars)`;
}

// JSON value equality as the corpus compares: numbers as doubles, members in any order.
export function valueEqual(a: unknown, b: unknown): boolean {
  if (Array.isArray(a)) return Array.isArray(b) && a.length === b.length && a.every((x, i) => valueEqual(x, b[i]));
  if (isObject(a)) {
    if (!isObject(b)) return false;
    const ka = Object.keys(a), kb = Object.keys(b);
    return ka.length === kb.length && ka.every((k) => Object.hasOwn(b, k) && valueEqual(a[k], b[k]));
  }
  return a === b;
}

export class Messages {
  private provided = new Map<string, unknown>();
  private revisions = new Map<number, number>();
  private epochs = new Map<string, Map<number, number>>();
  // The expected revisions first seen in the current group of unordered steps, with their route (null outside one).
  private group: Map<number, string | null> | null = null;
  route: string | null = null;

  // README "Comparing messages", Revisions: within a group of unordered steps, revisions first seen on different routes
  // may arrive in either order, since the order of the routes' publications is the producer's; they still differ.
  beginGroup() {
    this.group = new Map();
  }

  endGroup() {
    this.group = null;
  }

  constructor(private readonly ids: Record<string, Id>, private readonly provide: (name: string) => Promise<unknown>) {}

  id(name: string, ...kinds: string[]): Id {
    const id = this.ids[name];
    if (!id) throw new ScenarioFailure(`'${name}' is not declared in ids.`);
    if (!kinds.includes(id.kind)) throw new ScenarioFailure(`'${name}' is a ${id.kind} id, not a ${kinds.join(" or ")} id.`);
    return id;
  }

  // The text a producer runner's send step writes. A string message is the request's text.
  async write(message: unknown): Promise<string> {
    if (typeof message === "string") return message;
    this.route = isObject(message) && typeof message.route === "string" ? message.route : null;
    return this.writeValue(message, null);
  }

  private async writeValue(node: unknown, member: string | null): Promise<string> {
    if (isObject(node)) {
      if ("$any" in node) {
        if (!("default" in node)) throw new ScenarioFailure(`${abbreviate(node)} has no default`);
        return this.writeValue(node.default, member);
      }
      const keys = Object.keys(node);
      if (keys.length === 1 && keys[0] === "$id") {
        const name = String(node.$id);
        const id = this.id(name, "wire", "provided");
        if (id.kind === "wire") return JSON.stringify(id.value ?? "");
        if (!this.provided.has(name)) {
          const value = await this.provide(name);
          if (value === undefined || value === null)
            throw new ScenarioFailure(`the provided id '${name}' is written before the producer provided it`);
          this.provided.set(name, value);
        }
        return JSON.stringify(this.provided.get(name));
      }
      if (keys.length === 1 && keys[0] === "$json") return String(node.$json);
      const parts: string[] = [];
      for (const key of keys) {
        let text: string;
        const child = node[key];
        if (key === "epoch" && typeof child === "number" && this.epochsOf().has(child)) text = String(this.epochsOf().get(child));
        else text = await this.writeValue(child, key);
        parts.push(`${JSON.stringify(key)}:${text}`);
      }
      return `{${parts.join(",")}}`;
    }
    if (Array.isArray(node)) return `[${(await Promise.all(node.map((x) => this.writeValue(x, null)))).join(",")}]`;
    return JSON.stringify(node);
  }

  private epochsOf(): Map<number, number> {
    const route = this.route ?? "";
    let map = this.epochs.get(route);
    if (!map) this.epochs.set(route, (map = new Map()));
    return map;
  }

  match(expected: unknown, actual: unknown, route: string | null) {
    if (typeof expected === "string") expected = JSON.parse(expected);
    this.route = route;
    this.matchAt(expected, actual, "$", null);
  }

  // match for a candidate of an unordered group: null when equal, else the failure; bindings restored on failure.
  tryMatch(expected: unknown, actual: unknown, route: string | null): string | null {
    const saved = [new Map(this.provided), new Map(this.revisions), new Map([...this.epochs].map(([k, v]) => [k, new Map(v)])), this.group && new Map(this.group)] as const;
    try {
      this.match(expected, actual, route);
      return null;
    } catch (e) {
      [this.provided, this.revisions, this.epochs, this.group] = saved as any;
      if (e instanceof ScenarioFailure) return e.message;
      throw e;
    }
  }

  matchAt(expected: unknown, actual: unknown, path: string, member: string | null) {
    const fail = () => new ScenarioFailure(`${path}: expected ${abbreviate(expected)}, actual ${abbreviate(actual)}`);
    if (isObject(expected) && "$any" in expected) {
      const kind = String(expected.$any);
      const ok = typeOf(actual) === kind || (kind === "integer" && Number.isInteger(actual));
      if (!ok) throw fail();
      return;
    }
    if (isObject(expected) && Object.keys(expected).length === 1 && "$id" in expected) {
      const name = String(expected.$id);
      const id = this.id(name, "wire", "provided");
      if (id.kind === "wire") {
        if (actual !== id.value) throw new ScenarioFailure(`${path}: expected the wire id ${name} (${JSON.stringify(id.value)}), actual ${abbreviate(actual)}`);
      } else if (this.provided.has(name)) {
        if (!valueEqual(this.provided.get(name), actual)) throw fail();
      } else this.provided.set(name, actual);
      return;
    }
    if (isObject(expected)) {
      if (!isObject(actual)) throw fail();
      const missing = Object.keys(expected).filter((k) => !Object.hasOwn(actual, k));
      const extra = Object.keys(actual).filter((k) => !Object.hasOwn(expected, k));
      if (missing.length || extra.length)
        throw new ScenarioFailure(`${path}: members differ (missing [${missing}], unexpected [${extra}]); expected ${abbreviate(expected)}, actual ${abbreviate(actual)}`);
      if (path === "$" && typeof expected.route === "string") this.route = expected.route;
      const frame = expected.kind === "frame";
      for (const key of Object.keys(expected)) {
        if (frame && key === "changes") this.matchChanges(expected[key], actual[key], `${path}.changes`);
        else this.matchAt(expected[key], actual[key], `${path}.${key}`, key);
      }
      return;
    }
    if (Array.isArray(expected)) {
      if (!Array.isArray(actual) || actual.length !== expected.length) throw fail();
      expected.forEach((e, i) => this.matchAt(e, actual[i], `${path}[${i}]`, null));
      return;
    }
    if (typeof expected === "number" && (member === "revision" || member === "baseRevision")) {
      if (typeof actual !== "number") throw fail();
      return this.matchRevision(expected, actual, path);
    }
    if (typeof expected === "number" && member === "epoch") {
      if (typeof actual !== "number") throw fail();
      return this.matchEpoch(expected, actual, path);
    }
    if (!valueEqual(expected, actual)) throw fail();
  }

  // A frame's changes: per field in order, and the set of fields (README "Comparing messages").
  private matchChanges(expected: unknown, actual: unknown, path: string) {
    if (!Array.isArray(expected) || !Array.isArray(actual)) throw new ScenarioFailure(`${path}: changes are not arrays`);
    const byField = (changes: any[]) => {
      const m = new Map<string, any[]>();
      for (const c of changes) {
        if (!isObject(c) || typeof c.field !== "string") throw new ScenarioFailure(`${path}: a change without a field`);
        (m.get(c.field) ?? m.set(c.field, []).get(c.field)!).push(c);
      }
      return m;
    };
    const e = byField(expected), a = byField(actual);
    const ek = [...e.keys()].sort(), ak = [...a.keys()].sort();
    if (!valueEqual(ek, ak)) throw new ScenarioFailure(`${path}: the changed fields differ: expected [${ek}], actual [${ak}]; actual ${abbreviate(actual)}`);
    for (const [field, changes] of e) {
      const ac = a.get(field)!;
      if (ac.length !== changes.length)
        throw new ScenarioFailure(`${path}: field '${field}' has ${ac.length} changes, expected ${changes.length}; actual ${abbreviate(actual)}`);
      changes.forEach((c, i) => this.matchAt(c, ac[i], `${path}[${field}][${i}]`, null));
    }
  }

  private matchRevision(expected: number, actual: number, path: string) {
    const mapped = this.revisions.get(expected);
    if (mapped !== undefined) {
      if (mapped !== actual) throw new ScenarioFailure(`${path}: expected revision ${expected} was revision ${mapped} before, now ${actual}`);
      return;
    }
    for (const [other, otherActual] of this.revisions) {
      if (this.group?.has(other) && this.group.get(other) !== this.route) {
        if (otherActual === actual)
          throw new ScenarioFailure(`${path}: expected revision ${expected} maps to ${actual}, which expected revision ${other} of another route already was`);
        continue;
      }
      if ((other < expected && otherActual >= actual) || (other > expected && otherActual <= actual))
        throw new ScenarioFailure(`${path}: expected revision ${expected} maps to ${actual}, out of order with expected revision ${other}, which was ${otherActual}`);
    }
    this.revisions.set(expected, actual);
    this.group?.set(expected, this.route);
  }

  private matchEpoch(expected: number, actual: number, path: string) {
    const epochs = this.epochsOf();
    const mapped = epochs.get(expected);
    if (mapped !== undefined) {
      if (mapped !== actual) throw new ScenarioFailure(`${path}: expected epoch ${expected} was epoch ${mapped} before, now ${actual}`);
      return;
    }
    for (const a of epochs.values())
      if (a === actual) throw new ScenarioFailure(`${path}: expected epoch ${expected} is epoch ${actual}, which another expected epoch already was`);
    epochs.set(expected, actual);
  }
}

// The generators of README "Comparing messages": {"$rows": [start, count, width?]} and {"$adds": [...]}.
export function expand(node: unknown): unknown {
  if (Array.isArray(node)) {
    const out: unknown[] = [];
    for (const item of node) {
      const range = (name: string) => (isObject(item) && Object.keys(item).length === 1 && Array.isArray(item[name]) ? (item[name] as number[]) : null);
      const rows = range("$rows"), adds = range("$adds");
      if (rows) for (const id of ids(rows)) out.push(row(id, rows[2] ?? 0));
      else if (adds)
        for (const id of ids(adds))
          out.push({ field: "rows", kind: "add", index: id, oldIndex: -1, keys: [String(id)], items: [row(id, adds[2] ?? 0)] });
      else out.push(expand(item));
    }
    return out;
  }
  if (isObject(node)) return Object.fromEntries(Object.entries(node).map(([k, v]) => [k, expand(v)]));
  return node;
}

const ids = ([start, count]: number[]) => Array.from({ length: count }, (_, i) => start + i);
const row = (id: number, width: number) => ({ id, label: `row ${id}`.padEnd(width, ".") });
