import publishedRelease from './published-release.json';
import { createReleaseDocs } from './release-docs-core';

export {
  availabilityLabel,
  packageInstallCommand,
  versionLabel,
} from './release-docs-core';
export type { ReleaseVersion } from './release-docs-core';
export const currentRelease = publishedRelease;
export const { activeVersionForProduct, catalogRows } =
  createReleaseDocs(currentRelease);
export const releaseSummary = `Runic SDK ${currentRelease.version} is available from NuGet and npm. Install the components you need and keep Runic packages on the same preview version.`;
