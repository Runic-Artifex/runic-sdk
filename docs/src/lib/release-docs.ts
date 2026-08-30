import { releaseData } from '$lib/generated/release-data';
import {
  availabilityLabel,
  createReleaseDocs,
  packageInstallCommand,
  versionLabel,
  type ReleaseVersion,
} from './release-docs-core';

const releaseDocs = createReleaseDocs(releaseData);

export { availabilityLabel, packageInstallCommand, versionLabel };
export type { ReleaseVersion };
export const compatibilitySet = releaseData.compatibilitySet;

const exactCandidatePackages = (identities: readonly string[]) =>
  identities.map((identity) => {
    const candidate = compatibilitySet.packages.find(
      (entry) => entry.identity === identity,
    );
    if (!candidate) {
      throw new Error(`The compatibility set does not select ${identity}.`);
    }
    return `${candidate.identity}@${candidate.version}`;
  });

const examplesRepository = releaseData.repositories.find(
  (repository) => repository.id === 'examples',
)?.currentIdentity;
if (!examplesRepository) {
  throw new Error(
    'Release authority does not register the examples repository.',
  );
}

const candidateMaturity = `${compatibilitySet.releaseTrainVersion} local candidate; publication ${compatibilitySet.publication}`;
const candidatePrerequisites = `.NET SDK ${compatibilitySet.toolchain.dotnetSdk}, Node ${compatibilitySet.toolchain.node}, npm ${compatibilitySet.toolchain.npm}; ${compatibilitySet.platformProfiles.join(', ')}`;

export const choosePathRows = [
  {
    path: 'Generate a complete Desktop application',
    maturity: candidateMaturity,
    prerequisites: candidatePrerequisites,
    packages: exactCandidatePackages([
      'Runic.Application.Templates',
      'dotnet-runic',
    ]),
    start: `dotnet new runic-app-{react|vue|svelte|angular}; ${examplesRepository}`,
  },
  {
    path: 'Compose a custom Desktop host',
    maturity: candidateMaturity,
    prerequisites: candidatePrerequisites,
    packages: exactCandidatePackages([
      'Runic.Application',
      'Runic.Application.Bridge',
      'Runic.Application.Desktop',
      'Runic.Desktop',
      'Runic.Assets',
      'Runic.Assets.Desktop',
      '@runic-artifex/application-bridge',
      '@runic-artifex/desktop',
    ]),
    start: `Manual composition; use ${examplesRepository} as the executable reference`,
  },
  {
    path: 'Build a translation-focused Desktop application',
    maturity: candidateMaturity,
    prerequisites: candidatePrerequisites,
    packages: exactCandidatePackages([
      'Runic.Translations',
      'Runic.Translations.Build',
      'Runic.Application.Desktop',
      'Runic.Desktop',
      '@runic-artifex/vite-plugin-runic-translations',
      '@runic-artifex/desktop',
    ]),
    start: `Use ${examplesRepository} and the separately versioned Translations Editor application`,
  },
] as const;

export const {
  activeLaneForProduct,
  activeVersions,
  activeVersionForProduct,
  activeVersionsArePending,
  distributionsArePending,
  releaseSummary,
  releaseRows,
  catalogRows,
  migrationRows,
  compatibilityRows,
  distributionRows,
} = releaseDocs;
