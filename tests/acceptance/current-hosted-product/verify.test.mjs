import assert from "node:assert/strict";
import test from "node:test";
import { verifyReceipt } from "./verify.mjs";

const valid = { schema: "runic.current-hosted-product-repeat/1", journeys: Array.from({ length: 2 }, () => ({ schema: "runic.current-hosted-product/1", isolation: { nuget: ".nuget/packages", npm: ".npm-cache" }, generated: { catalog: "product", esmAbiVersion: 3, contractFingerprint: "sha256:" + "a".repeat(64) }, negativeGates: ["fingerprint-skew", "key-skew", "argument-skew", "stale-generated-output", "forged-generated-output"], localeEvidence: ["en-url-over-cookie", "de-url-over-cookie", "unsupported-locale", "hydration-mismatch"], nugetCandidates: Array.from({ length: 4 }, () => ({ source: "exact-local", archiveSha256: "a".repeat(64) })), npmCandidates: Array.from({ length: 3 }, () => ({ source: "exact-local", archiveSha256: "a".repeat(64) })), phases: ["restore", "tool-restore", "build", "npm-install", "svelte-check", "translation-contract", "ssr-locale", "hydration-bootstrap"].map(name => ({ name, status: "passed", exitCode: 0 })) })) };
test("accepts a complete product receipt", () => assert.equal(verifyReceipt(valid).ok, true));
test("fails closed for product contract skew", () => { const forged = structuredClone(valid); forged.journeys[1].negativeGates.pop(); assert.equal(verifyReceipt(forged).ok, false); });
