import assert from "node:assert/strict";
import test from "node:test";
import { verifyReceipt } from "./verify.mjs";
const journey = () => ({ schema: "runic.support-envelope-consumer/1", tool: { archive: "dotnet-runic.0.2.0-preview.w50001.nupkg", sha256: "a".repeat(64) }, editor: { binary: "RunicTranslations.Editor.dll", sha256: "b".repeat(64) }, isolatedCaches: true, noProductProjectReference: true, previewListsSelectionAndOmissions: true, collectByteIdentical: true, removed: true, hostileRejected: true, hostileRejections: ["workspace-root", "relative-path", "token", "source-text", "translation-text", "review-text"], outboundTransportAttempts: 0 });
const provenance = { tool: journey().tool, editor: journey().editor };
test("accepts exact local support evidence", () => assert.equal(verifyReceipt({ schema: "runic.support-envelope-consumer-repeat/1", journeys: [journey(), journey()] }, provenance).ok, true));
test("rejects forged or softened support evidence", () => { const value = { schema: "runic.support-envelope-consumer-repeat/1", journeys: [journey(), journey()] }; value.journeys[1].tool.sha256 = "forged"; value.journeys[1].outboundTransportAttempts = 1; value.journeys[1].hostileRejections.pop(); assert.equal(verifyReceipt(value, provenance).ok, false); });
