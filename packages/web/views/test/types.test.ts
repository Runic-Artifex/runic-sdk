import { test } from "node:test";
import {
  waitForBridge,
  type BridgeDiagnostic,
  type BridgeDiagnosticCode,
  type BridgeOperationDeliveryFailure,
  type BridgeOperationStartOptions,
  type BridgeOperationStatusKind,
  type BridgeOperationStreamPage,
  type PageReference,
} from "../dist/index.js";

// Compile-time checks of the 1.0 type shapes (W319-003 D21–D23, D31, D46 j); `bun run check`
// type-checks this file, so a regression fails the package test.
test("the public types have their 1.0 shapes", () => {
  const timedOut: BridgeOperationStatusKind = "timed-out";
  // @ts-expect-error The status token is kebab-case.
  const camel: BridgeOperationStatusKind = "timedOut";
  const code: BridgeDiagnosticCode = "version";
  // @ts-expect-error A diagnostic code is typed.
  const typo: BridgeDiagnostic["code"] = "timedOut";
  // @ts-expect-error A stream page never has the client-only `timed-out` kind.
  const page: BridgeOperationStreamPage<number>["kind"] = "timed-out";
  const delivery: BridgeOperationDeliveryFailure = { kind: "result-too-large", message: "Too large." };
  const start: BridgeOperationStartOptions = { requestId: "r" };
  const reference: PageReference<string> = { kind: "editor", connect: async () => "client" };
  // @ts-expect-error waitForBridge takes an options object only.
  const numeric = () => waitForBridge(5);
  void [timedOut, camel, code, typo, page, delivery, start, reference, numeric];
});
