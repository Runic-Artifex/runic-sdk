// One case of the Runic host protocol corpus: a scenario of the guest or the host role
// (specs/desktop/host-conformance/README.md). A role the implementation does not have is `excluded`, and a scenario
// whose `requires` names a feature the implementation does not list is `unsupported` (R2 DT-13): a guest's features are
// `describe`'s `features`, a host's its `hostFeatures` (the features of its hello), or `features` when it names none.
import type { Adapter, Outcome } from "../core";
import { GuestRun, prepareGuest } from "./guest";
import { HostRun } from "./host";
import { PAGE_TRANSPORTS } from "./page-transport";

export async function runHostCase(adapter: Adapter, scenario: any, supports: Set<string>, roles: Set<string>, timing: { receiveMs: number }): Promise<Outcome> {
  const role: string = scenario.roles[0];
  if (!roles.has(role)) return { outcome: "excluded", detail: `role:${role}` };
  const describe = adapter.describe ?? {};
  const features = role === "host" && Array.isArray(describe.hostFeatures) ? new Set<string>([...describe.hostFeatures, ...(describe.capabilities ?? [])]) : supports;
  const missing = (scenario.requires ?? []).filter((r: string) => !features.has(r));
  if (missing.length) return { outcome: "unsupported", detail: `the implementation lists no ${missing.join(", ")}`, requires: missing };
  if (role === "host") return new HostRun(adapter, scenario, { receiveMs: timing.receiveMs, guestActivationMs: Number(describe.guestActivationMs ?? 0) }).run();
  await prepareGuest();
  const transport = PAGE_TRANSPORTS[describe.pageTransport];
  return new GuestRun(adapter, scenario, { receiveMs: timing.receiveMs, transport }).run();
}
