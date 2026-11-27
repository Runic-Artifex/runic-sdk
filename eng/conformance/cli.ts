#!/usr/bin/env bun
// The language-neutral conformance driver (eng/conformance/README.md). From the repository root:
//
//   bun run conformance <implementation> [--only <text>] [--set <set>] [--jobs <n>] [--verbose] [--no-build]
//                                        [--receipt <file>] [--write-status] [--corpus <dir>] [--timeout-scale <x>]
//   bun run conformance list                            the implementations of implementations.json
//   bun run conformance cases [--write]                 check, or write, each corpus's cases.json
//   bun run conformance lint                            the status files' own rules and their case lists
//   bun run conformance check <implementation> [--receipt <file>]   compare a receipt with the status file
//
// <implementation> names an entry of implementations.json: its corpus, adapter build and command, and status file. A
// run builds the adapter, runs every case of the corpus through it, prints each failure with its rerun flag, writes a
// receipt to artifacts/conformance and compares the outcomes with the status file. It exits 1 on a blocking finding.
// The command is core/driver.ts; this file registers runic-sdk's corpus modules.
import { resolve } from "node:path";
import { loadAssetsCorpus } from "./assets/corpus";
import { runAssetsVector } from "./assets/read";
import { createDriver, type CorpusModule } from "./core/driver";
import { loadServerCorpus } from "./server/corpus";
import { runServerCase } from "./server/run";
import { loadViewsCorpus } from "./views/corpus";
import { runScenario } from "./views/scenarios";
import { runVector } from "./views/vectors";

export const ROOT = resolve(import.meta.dir, "../..");

// The corpus modules: one per corpus format the driver runs. A corpus's directory holds its cases.json and status/.
export const corpora: Record<string, CorpusModule> = {
  "views/4": {
    root: "specs/application/conformance",
    load: loadViewsCorpus,
    run: (adapter, c, { supports, roles, timing }) => (c.kind === "vector" ? runVector(adapter, c.body, roles, c.roles, supports) : runScenario(adapter, c.body, supports, timing, roles)),
  },
  "assets/2": {
    root: "specs/assets/vectors",
    load: loadAssetsCorpus,
    run: (adapter, c, { supports }) => runAssetsVector(adapter, c.body, supports),
  },
  "desktop-server/1": {
    root: "specs/desktop/server",
    load: loadServerCorpus,
    run: (adapter, c, { supports, timing }) => runServerCase(adapter, c.body, supports, timing),
  },
};

const driver = createDriver({ root: ROOT, dir: import.meta.dir, corpora });
export const { implementations, anchorExists } = driver;

if (import.meta.main) process.exit(await driver.main());
