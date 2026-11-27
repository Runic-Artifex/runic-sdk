// One case of the Runic host protocol corpus: a scenario of the guest or the host role
// (specs/desktop/host-conformance/README.md). A role the implementation does not have is `excluded`, and a scenario
// whose `requires` names a feature the implementation does not list in its hello is `unsupported` (R2 DT-13).
import type { Adapter, Outcome } from "../core";
import { GuestRun, prepareGuest } from "./guest";
import { PAGE_TRANSPORTS } from "./page-transport";

export async function runHostCase(adapter: Adapter, scenario: any, supports: Set<string>, roles: Set<string>, timing: { receiveMs: number }): Promise<Outcome> {
  const role: string = scenario.roles[0];
  if (!roles.has(role)) return { outcome: "excluded", detail: `role:${role}` };
  const missing = (scenario.requires ?? []).filter((r: string) => !supports.has(r));
  if (missing.length) return { outcome: "unsupported", detail: `the implementation lists no ${missing.join(", ")}`, requires: missing };
  if (role === "host") return { outcome: "unsupported", detail: "the driver has no host-role steps yet: the host role's runner is a later slice (R7 G20)" };
  await prepareGuest();
  const transport = PAGE_TRANSPORTS[adapter.describe?.pageTransport];
  return new GuestRun(adapter, scenario, { receiveMs: timing.receiveMs, transport }).run();
}
