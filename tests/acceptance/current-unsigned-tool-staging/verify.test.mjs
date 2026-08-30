import assert from "node:assert/strict";
import test from "node:test";
import { verifyReceipt } from "./verify.mjs";

const toolStaging = { schema: "runic.dotnet-runic-unsigned-staging/1", publication: "forbidden" };
const candidateSet = { schema: "runic.unsigned-candidate-set/1", publication: "forbidden" };
const journey = () => ({ schema: "runic.unsigned-tool-staging-consumer/1", isolation: { dotnetCliHome: ".dotnet", nugetPackages: ".nuget/packages", nugetHttpCache: ".nuget/http" }, projectReferences: [], remoteSources: [], supportEnvelopeContent: "forbidden", canonicalReleaseApproval: "seven-package-release-gate-required", toolStaging, candidateSet, command: { output: "dotnet-runic" } });

test("accepts deterministic direct-tool staging evidence", () => {
  const receipt = { schema: "runic.unsigned-tool-staging-consumer-repeat/1", journeys: [journey(), journey()] };
  assert.deepEqual(verifyReceipt(receipt, { toolStaging, candidateSet }), { ok: true, errors: [] });
});
test("rejects full-train substitution, source references, remote sources, and support content", () => {
  const receipt = { schema: "runic.unsigned-tool-staging-consumer-repeat/1", journeys: [journey(), journey()] };
  receipt.journeys[1].canonicalReleaseApproval = "approved";
  receipt.journeys[1].projectReferences = ["tool.csproj"];
  receipt.journeys[1].remoteSources = ["https://api.nuget.org/v3/index.json"];
  receipt.journeys[1].supportEnvelopeContent = { payload: "forged" };
  receipt.journeys[1].supportEnvelope = { payload: "forged" };
  assert.equal(verifyReceipt(receipt, { toolStaging, candidateSet }).ok, false);
});
