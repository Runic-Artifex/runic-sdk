import assert from "node:assert/strict";
import test from "node:test";
import {
  NPM_CANDIDATES,
  NUGET_CANDIDATES,
  RECEIPT_SCHEMA,
  REPEAT_RECEIPT_SCHEMA,
  TOPOLOGY,
  TOPOLOGY_SHA256,
  verifyReceipt,
  verifyRepeatedReceipt,
  verifyTopology,
} from "./verify.mjs";

const phase = (name, argv, status = "passed") => ({
  name,
  argv,
  status,
  exitCode: status === "passed" ? 0 : 1,
  reasonCode: status === "passed" ? null : "command-exit-nonzero",
});

const receipt = () => ({
  schema: RECEIPT_SCHEMA,
  topology: TOPOLOGY,
  topologySha256: TOPOLOGY_SHA256,
  isolation: {
    nugetGlobalPackagesFolder: ".nuget/packages",
    nugetHttpCachePath: ".nuget/http-cache",
    dotnetCliHome: ".dotnet",
    npmCache: ".npm-cache",
  },
  nugetCandidates: NUGET_CANDIDATES.map((candidate) => ({
    ...candidate,
    source: "w30-004-local-candidate-nuget-feed",
    contentHash: "sha512-fixture",
  })),
  npmCandidates: NPM_CANDIDATES.map((candidate) => ({
    ...candidate,
    source: "w30-004-local-candidate-npm-feed",
    archiveSha256: "b".repeat(64),
    integrity: "sha512-fixture",
  })),
  ejection: {
    service: "service",
    frontend: "frontend/build",
    staticAssetsOwner: "sveltekit",
    topologyBound: true,
    secretAbsent: true,
  },
  phases: [
    phase("restore", ["dotnet", "restore", "HostedDeployment.csproj", "--configfile", "NuGet.config", "--no-cache", "--force-evaluate", "--nologo"]),
    phase("publish", ["dotnet", "publish", "HostedDeployment.csproj", "--no-restore", "--configuration", "Release", "--output", "<ejected-service>", "--nologo"]),
    phase("frontend-install", ["npm", "install", "--ignore-scripts", "--legacy-peer-deps"]),
    phase("frontend-check", ["npm", "run", "check"]),
    phase("frontend-build", ["npm", "run", "build"]),
    phase("ejected-frontend-install", ["npm", "install", "--omit=dev", "--ignore-scripts", "--legacy-peer-deps"]),
    phase("missing-secret", ["dotnet", "<ejected-service>"], "failed"),
    phase("unsafe-public-origin", ["dotnet", "<ejected-service>"], "failed"),
    phase("service-health-readiness", ["dotnet", "<ejected-service>"]),
    phase("frontend-ssr", ["node", "<ejected-frontend-server>"]),
  ],
});

test("hosted deployment topology accepts the explicit initial shape", () => {
  assert.deepEqual(verifyTopology(TOPOLOGY), { ok: true, errors: [] });
});

test("hosted deployment topology rejects public bridge or omitted secret injection", () => {
  const value = structuredClone(TOPOLOGY);
  value.proxy.publicApplicationBridgeWebSocket = true;
  delete value.secret;
  const report = verifyTopology(value);
  assert.equal(report.ok, false);
  assert.match(report.errors.join("\n"), /WebSocket|secret/);
});

test("hosted deployment receipt accepts exact ejection evidence", () => {
  assert.deepEqual(verifyReceipt(receipt()), { ok: true, errors: [] });
});

test("hosted deployment receipt rejects forged provenance and unsafe configuration evidence", () => {
  const value = receipt();
  value.nugetCandidates[0].source = "public";
  value.ejection.secretAbsent = false;
  value.phases[6].status = "passed";
  const report = verifyReceipt(value);
  assert.equal(report.ok, false);
  assert.match(report.errors.join("\n"), /NuGet|secret|missing-secret/);
});

test("hosted deployment receipt requires byte-identical journeys", () => {
  const value = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [receipt(), receipt()] };
  assert.deepEqual(verifyRepeatedReceipt(value), { ok: true, errors: [] });
  value.journeys[1].ejection.frontend = "forged";
  assert.match(verifyRepeatedReceipt(value).errors.join("\n"), /not deterministic/);
});
