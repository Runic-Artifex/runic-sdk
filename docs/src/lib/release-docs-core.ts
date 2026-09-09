export type ReleaseVersion = {
  readonly state: 'published' | 'unpublished' | 'unassigned';
  readonly value: string | null;
};
export type InstallKind =
  'nuget-package' | 'dotnet-template' | 'dotnet-tool' | 'npm-package';
export type PublishedRelease = {
  readonly version: string;
  readonly url: string;
  readonly packages: readonly {
    readonly identity: string;
    readonly ecosystem: string;
    readonly product: string;
    readonly installKind: string;
  }[];
};
export function versionLabel(version: ReleaseVersion | undefined) {
  return version?.value ?? 'Version unassigned';
}
export function availabilityLabel(version: ReleaseVersion | undefined) {
  return version?.state === 'published' ? 'Published' : 'Not published';
}
export function packageInstallCommand(entry: {
  readonly name: string;
  readonly installKind?: string;
  readonly version: ReleaseVersion | undefined;
}) {
  if (entry.version?.state !== 'published' || !entry.version.value)
    return undefined;
  switch (entry.installKind) {
    case 'nuget-package':
      return `dotnet add package ${entry.name} --version ${entry.version.value}`;
    case 'dotnet-template':
      return `dotnet new install ${entry.name}::${entry.version.value}`;
    case 'dotnet-tool':
      return `dotnet tool install --local ${entry.name} --version ${entry.version.value}`;
    case 'npm-package':
      return `npm install --save-exact ${entry.name}@${entry.version.value}`;
  }
}
export function createReleaseDocs(release: PublishedRelease) {
  const version: ReleaseVersion = {
    state: 'published',
    value: release.version,
  };
  const catalogRows = release.packages.map((entry) => ({
    name: entry.identity,
    registry: entry.ecosystem === 'nuget' ? 'NuGet' : 'npm',
    registryUrl:
      entry.ecosystem === 'nuget'
        ? `https://www.nuget.org/packages/${entry.identity}/${release.version}`
        : `https://www.npmjs.com/package/${entry.identity}/v/${release.version}`,
    productId: entry.product,
    product: entry.product,
    installKind: entry.installKind,
    version,
  }));
  return {
    catalogRows,
    activeVersionForProduct: (product: string) =>
      catalogRows.some((row) => row.productId === product)
        ? version
        : undefined,
  };
}
