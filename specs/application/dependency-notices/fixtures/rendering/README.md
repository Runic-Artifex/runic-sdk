# Rendering fixtures

These schema-version-2 fixtures are byte goldens for the deterministic Dependency Notices rendering pipeline. They cover the canonical JSON document, plain-text third-party notices, standalone semantic HTML, and the generation manifest.

The expected files are intentionally marked `-text` so Git never changes their UTF-8-without-BOM, LF-only byte sequences. The executable rendering tests construct an adversarial in-memory document, render it under multiple cultures and input orders, and compare all four files byte for byte. The evidence body appears in two component records with the same lowercase SHA-256: each component retains a digest-linked reference while the text and HTML appendices print the body once. Before emitting bytes, the renderer recomputes this digest over the strict UTF-8 encoding of the evidence text and validates canonical Package URLs, SPDX expressions, required fields, enums, and schema-v2 relationships.

The complete document contract begins at `schemaVersion: 2`; the Wave A v1 schema remains unchanged. The manifest has an independent `schemaVersion: 1` contract.
