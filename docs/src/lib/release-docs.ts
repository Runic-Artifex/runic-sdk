import activeSdkRelease from './active-sdk-release.json';
import publishedRelease from './published-release.json';
import { createReleaseDocs } from './release-docs-core';

export {
  availabilityLabel,
  packageInstallCommand,
  versionLabel,
} from './release-docs-core';
export type { ReleaseVersion } from './release-docs-core';
// This is the SDK's active catalog. It deliberately excludes independently
// released Runic products.
export const currentRelease = activeSdkRelease;
// The former unified catalog remains factual release history. Do not rewrite it
// when product ownership changes.
export const historicalRelease = publishedRelease;
export const { activeVersionForProduct, catalogRows } =
  createReleaseDocs(currentRelease);
export const historicalCatalogRows =
  createReleaseDocs(historicalRelease).catalogRows;
export const releaseSummary = `Runic SDK ${currentRelease.version} is available from NuGet and npm. Install the SDK components you need and keep SDK packages on the same preview version.`;
