#!/usr/bin/env node
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const policy = text("src/Runic.Application.Hosting/HostedServiceAdmissionPolicy.cs");
const guide = text("docs/guides/hosted-service.md");
const deployment = text("src/Runic.Application.Hosting/HostedDeploymentConfiguration.cs");
const deploymentGuide = text("docs/guides/hosted-deployment.md");
for (const value of ["oidc-authorization-code", "__Host-runic-session", "X-Runic-CSRF", "/runic/service", "/signin-oidc"]) {
  requireText(policy, value, "C# admission policy");
  requireText(guide, value, "hosted-service guidance");
}
requireText(guide, "never use a\nRunic bearer-token carrier", "no-bearer service policy");
requireText(guide, "remains a local package-consumer proof", "W20 boundary deferral");
for (const value of ["Runic:HostedDeployment", "TrustedProxyAddresses", "StaticAssetsPath", "OidcClientSecret", "/runic/health", "/runic/ready"]) {
  requireText(deployment, value, "C# deployment configuration");
  requireText(deploymentGuide, value, "hosted-deployment guidance");
}
requireText(deploymentGuide, "must not add CORS\nor route the W20 Application Bridge WebSocket", "deployment W20/CORS deferral");
requireText(deploymentGuide, "cloud vendor", "deployment platform deferral");
console.log("Hosted service admission policy guidance passed.");

function text(path) {
  const absolute = resolve(root, path);
  if (!existsSync(absolute)) throw new Error(`Missing required hosted-service artifact '${path}'.`);
  return readFileSync(absolute, "utf8");
}
function requireText(source, expected, label) {
  if (!source.includes(expected)) throw new Error(`Missing ${label}: '${expected}'.`);
}
