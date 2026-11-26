// The Views vectors (specs/application/conformance/README.md, "Vectors") through an adapter. The driver decodes the
// input encoding, sends bytes, and compares; the adapter only calls its implementation's reader and writer and maps
// its errors to the corpus codes.
import type { Adapter } from "../core/adapter";
import { AdapterError } from "../core/adapter";
import type { Outcome } from "../core/results";
import { abbreviate, valueEqual } from "./messages";

function inputBytes(input: any): Uint8Array {
  switch (input.encoding) {
    case "utf8":
      return new TextEncoder().encode(input.value);
    case "base64":
      return Uint8Array.from(atob(input.value), (c) => c.charCodeAt(0));
    default:
      return new TextEncoder().encode(JSON.stringify(input.value ?? null));
  }
}

const b64 = (bytes: Uint8Array) => btoa(String.fromCharCode(...bytes));

// caseRoles are the roles of the case's file (README "Vectors"): an implementation with none of them excludes it.
export async function runVector(adapter: Adapter, testCase: any, roles: Set<string>, caseRoles: string[]): Promise<Outcome> {
  const { operation, input, expect } = testCase;
  if (!caseRoles.some((role) => roles.has(role))) return { outcome: "excluded", detail: `role:${caseRoles.join(",")}` };
  try {
    switch (operation) {
      case "canonicalize": {
        const r = await adapter.call("views.canonicalize", { input: b64(inputBytes(input)) });
        if (expect.output) {
          if (r.error) return { outcome: "fail", detail: `error ${r.error}, expected ${abbreviate(expect.output.value)}` };
          return r.output === expect.output.value ? { outcome: "pass" } : { outcome: "fail", detail: `wrote ${abbreviate(r.output)}, expected ${abbreviate(expect.output.value)}` };
        }
        if (!r.error) return { outcome: "fail", detail: `wrote ${abbreviate(r.output)}, expected the error ${expect.error.code}` };
        return r.error === expect.error.code ? { outcome: "pass" } : { outcome: "fail", detail: `error ${r.error}, expected ${expect.error.code}` };
      }
      case "decode": {
        const args = { type: testCase.type, parameters: testCase.parameters ?? {} };
        const r = await adapter.call("views.decode", { ...args, input: b64(inputBytes(input)) });
        const expected = "value" in expect ? expect.value : typeof expect.text === "string" ? JSON.parse(expect.text) : undefined;
        if (expected === undefined) {
          return r.error === "invalid-value" ? { outcome: "pass" } : { outcome: "fail", detail: `decoded to ${abbreviate(r.value)}, expected invalid-value` };
        }
        if (r.error) return { outcome: "fail", detail: `${r.error}, expected ${abbreviate(expected)}` };
        if (!valueEqual(JSON.parse(r.value), expected)) return { outcome: "fail", detail: `wrote ${abbreviate(r.value)}, expected ${abbreviate(expected)}` };
        // The other direction: the normal form reads back to itself.
        const again = await adapter.call("views.decode", { ...args, input: b64(new TextEncoder().encode(r.value)) });
        if (again.error || !valueEqual(JSON.parse(again.value), expected))
          return { outcome: "fail", detail: `the normal form does not read back: ${again.error ?? abbreviate(again.value)}` };
        return { outcome: "pass" };
      }
      case "read-request": {
        // The hello of the case's file, sent first on the fresh connection unless the case is a hello (README "Messages").
        const hello = b64(new TextEncoder().encode(JSON.stringify(testCase.hello)));
        const r = await adapter.call("views.readRequest", { input: b64(inputBytes(input)), inputBytes: testCase.limits?.inputBytes ?? null, hello });
        if (expect.result === "valid")
          return r.result === "valid" ? { outcome: "pass" } : { outcome: "fail", detail: `rejected: ${r.kind} ${r.reason}` };
        if (r.result === "valid") return { outcome: "fail", detail: `accepted, expected ${expect.error.kind} ${expect.error.reason}` };
        return r.kind === expect.error.kind && r.reason === expect.error.reason
          ? { outcome: "pass" }
          : { outcome: "fail", detail: `${r.kind} ${r.reason}, expected ${expect.error.kind} ${expect.error.reason}` };
      }
      case "read-push":
      case "read-reply": {
        const r =
          operation === "read-push"
            ? await adapter.call("views.readPush", { input: b64(inputBytes(input)) })
            : await adapter.call("views.readReply", { request: testCase.request, input: b64(inputBytes(input)) });
        return r.result === expect.result ? { outcome: "pass" } : { outcome: "fail", detail: `${r.result}, expected ${expect.result}` };
      }
      default:
        return { outcome: "fail", detail: `unknown operation ${operation}` };
    }
  } catch (e) {
    if (e instanceof AdapterError && e.code === "unsupported") return { outcome: "unsupported", detail: e.message };
    throw e;
  }
}
