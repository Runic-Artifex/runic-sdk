#!/usr/bin/env bun
// The conformance adapter of the TypeScript page client, @runic-artifex/views (PROTOCOL.md), in the client role. It
// reads the client message vectors (read-push, read-reply) with the package's protocol reader, from its source. The
// client scenarios and the value vectors need the client half of the driver's step engine and a value codec op (R7
// G10); until then the adapter answers `unsupported` for them. G10 can move it next to the package's tests.
import { createInterface } from "node:readline";
import { readPush, readReply } from "../../../packages/web/views/src/protocol";
import packageJson from "../../../packages/web/views/package.json";

const text = (base64: string) => Buffer.from(base64, "base64").toString("utf8");

function handle(op: string, args: any): unknown {
  switch (op) {
    case "describe":
      return {
        protocol: "rax.conformance-adapter/1",
        implementation: { id: "ts-client", name: packageJson.name, version: packageJson.version },
        corpora: ["views/4"],
        roles: ["client"],
        features: [],
        capabilities: [],
      };
    case "shutdown":
      return {};
    case "views.readPush":
      return { result: readPush(text(args.input)).result };
    case "views.readReply":
      return { result: readReply(args.request, text(args.input)) === undefined ? "invalid" : "valid" };
    default:
      throw Object.assign(new Error(`The TypeScript client adapter has no operation ${op} yet (R7 G10).`), { code: "unsupported" });
  }
}

const write = (message: unknown) => process.stdout.write(JSON.stringify(message) + "\n");
for await (const line of createInterface({ input: process.stdin })) {
  if (!line.trim()) continue;
  const { seq, op, args } = JSON.parse(line);
  try {
    write({ seq, ok: handle(op, args ?? {}) });
  } catch (e: any) {
    write({ seq, error: { code: e.code ?? "failed", message: String(e.message ?? e) } });
  }
  if (op === "shutdown") break;
}
