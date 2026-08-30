#!/usr/bin/env node
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const examplesRoot = resolve(here, "../..");
const editorRoot = resolve(examplesRoot, "../runic-translations-editor");

export const RECEIPT_SCHEMA = "runic.w40-localization-compatibility/1";
export const REPEAT_RECEIPT_SCHEMA = "runic.w40-localization-compatibility-repeat/1";
export const PORTABLE_BOUNDARY = {
  mf2Profile: "runic-mf2-subset/1",
  esmAbiVersion: 2,
  typedReferenceWireVersion: 1,
  interchange: {
    xliff: "XLIFF-2.1-text",
    reviewSidecar: "runic.translations.interchange-review/1",
    structuredContent: "loss-recorded-or-rejected",
  },
};
export const OWNERSHIP_BOUNDARY = {
  host: "C# owns identity, session, admission, and sanitized typed product data.",
  hostedRenderer: "Svelte renders only the sanitized projection with request-local URL-first locale routing.",
  localBridge: "The W20 ApplicationBridge transport is local-only and is not a hosted route.",
  nativeCertification: "W70 native-platform certification is outside this compatibility evidence.",
};

const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const sha256 = (value) => createHash("sha256").update(value).digest("hex");
const hash = (value) => typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
const fingerprint = (value) => typeof value === "string" && /^sha256:[a-f0-9]{64}$/.test(value);

async function receipt(path) {
  const source = await readFile(resolve(path), "utf8");
  return { path: resolve(path), sha256: sha256(source), value: JSON.parse(source) };
}

async function verifiers() {
  const [portable, hosted, desktop] = await Promise.all([
    import(pathToFileURL(resolve(examplesRoot, "eng/current-mf2-subset-consumer/verify.mjs")).href),
    import(pathToFileURL(resolve(examplesRoot, "eng/current-hosted-product/verify.mjs")).href),
    import(pathToFileURL(resolve(editorRoot, "eng/verify-localized-desktop-product.mjs")).href),
  ]);
  return { portable, hosted, desktop };
}

function candidate(journey, identity) {
  return journey.nugetCandidates?.find((item) => item.identity === identity);
}

function validateBoundary(portable, hosted, desktop) {
  const errors = [];
  const source = portable.journeys?.[0];
  const web = hosted.journeys?.[0];
  const editor = desktop.journeys?.[0];

  if (source?.profile !== PORTABLE_BOUNDARY.mf2Profile || source?.manifest?.esmAbiVersion !== PORTABLE_BOUNDARY.esmAbiVersion || !same(source?.negativeGates, ["unsupported-full-mf2", "abi-mismatch", "schema-mismatch", "stale-generated-manifest", "forged-generated-manifest"])) errors.push("portable MF2-subset evidence mismatch");
  if (web?.generated?.esmAbiVersion !== PORTABLE_BOUNDARY.esmAbiVersion || !fingerprint(web?.generated?.contractFingerprint) || !same(web?.negativeGates, ["fingerprint-skew", "key-skew", "argument-skew", "stale-generated-output", "forged-generated-output"]) || !same(web?.localeEvidence, ["en-url-over-cookie", "de-url-over-cookie", "unsupported-locale", "hydration-mismatch"])) errors.push("hosted typed-reference or request-local locale evidence mismatch");
  if (editor?.generated?.esmAbiVersion !== PORTABLE_BOUNDARY.esmAbiVersion || !fingerprint(editor?.generated?.contractFingerprint) || !same(editor?.negativeGates, ["missing-manifest", "stale-manifest", "forged-manifest-schema", "unsupported-locale", "fingerprint-skew"]) || !same(editor?.localeEvidence, ["en", "de", "structured-interchange"]) || !editor?.phases?.some((phase) => phase.name === "editor-interchange-smoke" && phase.status === "passed" && phase.exitCode === 0)) errors.push("desktop interchange or locale evidence mismatch");
  if (web?.generated?.contractFingerprint === editor?.generated?.contractFingerprint) errors.push("profile catalog fingerprints must remain catalog-specific");

  const hostedBuild = candidate(web ?? {}, "RunicTranslations.Build");
  const desktopBuild = candidate(editor ?? {}, "RunicTranslations.Build");
  if (hostedBuild?.source !== "exact-local" || desktopBuild?.source !== "exact-local" || hostedBuild?.version !== "0.1.0-preview.7cfaa0e" || desktopBuild?.version !== "0.1.0-preview.7cfaa0e" || !hash(hostedBuild?.archiveSha256) || hostedBuild.archiveSha256 !== desktopBuild?.archiveSha256) errors.push("shared portable Build candidate provenance mismatch");
  if (web?.npmCandidates?.find((item) => item.identity === "@runic-artifex/vite-plugin-runic-translations")?.version !== "0.1.0-preview.7cfaa0e" || editor?.npmCandidate?.identity !== "@runic-artifex/vite-plugin-runic-translations" || editor.npmCandidate?.version !== "0.1.0-preview.7cfaa0e") errors.push("shared ESM consumer package provenance mismatch");
  return errors;
}

export async function linkReceipts(portablePath, hostedPath, desktopPath) {
  const [portable, hosted, desktop, checks] = await Promise.all([receipt(portablePath), receipt(hostedPath), receipt(desktopPath), verifiers()]);
  const errors = [
    ...checks.portable.verifyReceipt(portable.value).errors,
    ...checks.hosted.verifyReceipt(hosted.value).errors,
    ...checks.desktop.verifyReceipt(desktop.value).errors,
    ...validateBoundary(portable.value, hosted.value, desktop.value),
  ];
  if (errors.length) throw new Error(errors.join("\n"));
  return {
    schema: RECEIPT_SCHEMA,
    portableBoundary: PORTABLE_BOUNDARY,
    ownershipBoundary: OWNERSHIP_BOUNDARY,
    inputs: {
      portable: { path: portable.path, sha256: portable.sha256, schema: portable.value.schema },
      hosted: { path: hosted.path, sha256: hosted.sha256, schema: hosted.value.schema },
      desktop: { path: desktop.path, sha256: desktop.sha256, schema: desktop.value.schema },
    },
    profiles: {
      hosted: { catalog: hosted.value.journeys[0].generated.catalog, contractFingerprint: hosted.value.journeys[0].generated.contractFingerprint, localeEvidence: hosted.value.journeys[0].localeEvidence },
      desktop: { catalog: desktop.value.journeys[0].generated.catalog, contractFingerprint: desktop.value.journeys[0].generated.contractFingerprint, localeEvidence: desktop.value.journeys[0].localeEvidence },
    },
  };
}

export function verifyReceipt(receipt) {
  const errors = [];
  if (receipt?.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt.journeys) || receipt.journeys.length !== 2) errors.push("two compatibility-link journeys are required");
  for (const journey of receipt?.journeys ?? []) {
    if (journey?.schema !== RECEIPT_SCHEMA || !same(journey.portableBoundary, PORTABLE_BOUNDARY) || !same(journey.ownershipBoundary, OWNERSHIP_BOUNDARY)) errors.push("compatibility boundary mismatch");
    if (!hash(journey?.inputs?.portable?.sha256) || !hash(journey?.inputs?.hosted?.sha256) || !hash(journey?.inputs?.desktop?.sha256)) errors.push("input receipt provenance mismatch");
    if (journey?.profiles?.hosted?.catalog !== "product" || journey?.profiles?.desktop?.catalog !== "editor" || !fingerprint(journey?.profiles?.hosted?.contractFingerprint) || !fingerprint(journey?.profiles?.desktop?.contractFingerprint) || journey.profiles.hosted.contractFingerprint === journey.profiles.desktop.contractFingerprint) errors.push("profile identity mismatch");
  }
  if (receipt?.journeys?.length === 2 && !same(receipt.journeys[0], receipt.journeys[1])) errors.push("compatibility-link journeys are not deterministic");
  return { ok: errors.length === 0, errors };
}

export async function runTwice(portablePath, hostedPath, desktopPath) {
  const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [await linkReceipts(portablePath, hostedPath, desktopPath), await linkReceipts(portablePath, hostedPath, desktopPath)] };
  const report = verifyReceipt(receipt);
  if (!report.ok) throw new Error(report.errors.join("\n"));
  return receipt;
}

async function main() {
  const [command, portablePath, hostedPath, desktopPath, receiptPath] = process.argv.slice(2);
  if (command === "run-twice" && portablePath && hostedPath && desktopPath && !receiptPath) process.stdout.write(JSON.stringify(await runTwice(portablePath, hostedPath, desktopPath), null, 2) + "\n");
  else if (command === "verify-twice" && portablePath && hostedPath && desktopPath && receiptPath) {
    const expected = await runTwice(portablePath, hostedPath, desktopPath);
    const actual = JSON.parse(await readFile(receiptPath, "utf8"));
    const report = verifyReceipt(actual);
    if (!report.ok || !same(actual, expected)) throw new Error(report.errors.concat("compatibility receipt differs from its sources").join("\n"));
  } else throw new Error("Usage: node eng/current-localization-compatibility/verify.mjs run-twice <portable-receipt> <hosted-receipt> <desktop-receipt> | verify-twice <portable-receipt> <hosted-receipt> <desktop-receipt> <receipt>");
}

if (import.meta.main) main().catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
