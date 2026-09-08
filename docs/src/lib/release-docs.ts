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
export const currentCandidate = releaseData.currentCandidate;
export const previewGuideUrl =
  'https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/releases/0.2.0-preview.1.md';
export const candidateCatalogRows = currentCandidate.packages.map((entry) => ({
  name: entry.identity,
  registry: entry.ecosystem === 'nuget' ? 'NuGet' : 'npm',
  version: entry.version,
}));

const exactCandidatePackages = (identities: readonly string[]) =>
  identities.map((identity) => {
    const candidate = currentCandidate.packages.find(
      (entry) => entry.identity === identity,
    );
    if (!candidate) {
      throw new Error(`The compatibility set does not select ${identity}.`);
    }
    return `${candidate.identity}@${candidate.version}`;
  });

const examplesRepository = 'runic-sdk/examples';
const candidateMaturity = `${currentCandidate.version} candidate; ${currentCandidate.publication}`;
const candidatePrerequisites = `.NET SDK ${currentCandidate.toolchain.dotnetSdk}; Bun ${currentCandidate.toolchain.bun}; Node ${currentCandidate.toolchain.node} for npm/pnpm compatibility checks`;

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
    start: `Use ${examplesRepository} for translation SDK integration; standalone Editor distributions are outside this preview`,
  },
] as const;

export const {
  activeVersionForProduct,
  activeVersionsArePending,
  catalogRows,
} = releaseDocs;

export const releaseSummary = `Runic SDK ${currentCandidate.version} is an unpublished candidate: ${currentCandidate.packages.filter((entry) => entry.ecosystem === 'nuget').length} NuGet packages and ${currentCandidate.packages.filter((entry) => entry.ecosystem === 'npm').length} npm packages. Publication awaits required CI, native acceptance, performance, two-hour soak and registry gates.`;
