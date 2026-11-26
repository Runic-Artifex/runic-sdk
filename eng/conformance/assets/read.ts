// One Assets archive vector through an adapter (specs/assets/vectors/README.md, "Running a case"; PROTOCOL.md,
// "Assets"). The adapter reads the archive in one mode and opens every asset it reports; the driver compares.
import { AdapterError, type Adapter, type Outcome } from "../core";

export const MODES = ["eager", "lazy"] as const;
type Mode = (typeof MODES)[number];

// The members a reader reports for each asset, compared in this order.
const MEMBERS = ["path", "mediaType", "length", "sha256", "entryPoint", "cacheMode"] as const;

// The reading modes an implementation has are its `eager-reading` and `lazy-reading` capabilities; one with
// `error-codes` reports the spec's codes, which are then compared. A case passes when it passes in every mode.
export async function runAssetsVector(adapter: Adapter, testCase: any, supports: Set<string>): Promise<Outcome> {
  const modes = MODES.filter((mode) => supports.has(`${mode}-reading`));
  if (!modes.length) return { outcome: "unsupported", detail: "the implementation lists neither eager-reading nor lazy-reading", requires: ["eager-reading"] };
  const codes = supports.has("error-codes");
  const failures: string[] = [];
  for (const mode of modes) {
    const expect = mode === "lazy" ? (testCase.expectLazy ?? testCase.expect) : testCase.expect;
    let reply: any;
    try {
      reply = await adapter.call("assets.read", { archive: testCase.input.value, mode });
    } catch (e) {
      if (e instanceof AdapterError && e.code === "unsupported") return { outcome: "unsupported", detail: e.message };
      throw e;
    }
    const problem = compare(mode, expect, reply, codes);
    if (problem) failures.push(`${mode}: ${problem}`);
  }
  return failures.length ? { outcome: "fail", detail: failures.join("\n") } : { outcome: "pass" };
}

// What is wrong with one mode's reply, or null.
export function compare(mode: Mode, expect: any, reply: any, codes: boolean): string | null {
  const said = reply?.message ? ` (${reply.message})` : "";
  if (expect.result === "valid") {
    if (reply?.result !== "valid") return `rejected at ${reply?.stage ?? "?"}${reply?.error ? ` with ${reply.error}` : ""}${said}, expected valid`;
    const actual: any[] = reply.assets ?? [];
    const row = (asset: any) => MEMBERS.map((member) => JSON.stringify(asset?.[member])).join(" ");
    if (actual.length !== expect.assets.length || expect.assets.some((asset: any, i: number) => row(asset) !== row(actual[i])))
      return `reported [${actual.map(row).join("; ")}], expected [${expect.assets.map(row).join("; ")}]`;
    for (const asset of actual) {
      if (asset.served?.length !== asset.length) return `served ${asset.served?.length} bytes of ${asset.path}, not its length ${asset.length}`;
      // A lazy reader does not check digests (archive-v1.md, "Eager and lazy reading").
      if (mode === "eager" && asset.served?.sha256 !== asset.sha256) return `served ${asset.path} with the digest ${asset.served?.sha256}, not ${asset.sha256}`;
    }
    return null;
  }
  if (reply?.result === "valid") return `accepted, expected ${expect.error}`;
  if (reply?.result !== "invalid") return `the result ${JSON.stringify(reply?.result)}`;
  // An eager reader checks every rule before it returns.
  if (mode === "eager" && reply.stage !== "read") return `the read returned and an asset then failed to open${said}, expected ${expect.error} from the read`;
  if (typeof reply.error === "string" && reply.error.startsWith("unmapped:")) return `${reply.error}${said}, expected ${expect.error}`;
  if (codes && reply.error !== expect.error) return `${reply.error ?? "no code"}${said}, expected ${expect.error}`;
  return null;
}
