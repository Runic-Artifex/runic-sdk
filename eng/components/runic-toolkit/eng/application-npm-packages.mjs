export const applicationNpmPackages = Object.freeze([
  Object.freeze({ directory: "application-bridge", identity: "@runic-artifex/application-bridge" }),
  Object.freeze({ directory: "application-bridge-tooling", identity: "@runic-artifex/application-bridge-tooling" }),
  Object.freeze({ directory: "angular", identity: "@runic-artifex/angular" }),
]);

export const applicationNpmPackageIdentities = Object.freeze(
  applicationNpmPackages.map(({ identity }) => identity),
);

if (import.meta.url === `file://${process.argv[1]}`) {
  process.stdout.write(String(applicationNpmPackages.length));
}
