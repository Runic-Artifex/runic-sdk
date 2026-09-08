export type ReleaseVersion = {
  readonly state: 'published' | 'unpublished' | 'unassigned';
  readonly value: string | null;
};
export type InstallKind =
  'nuget-package' | 'dotnet-template' | 'dotnet-tool' | 'npm-package';
export type ReleaseData = {
  readonly currentCandidate: {
    readonly version: string;
    readonly publication: 'unpublished';
    readonly packages: readonly {
      readonly identity: string;
      readonly ecosystem: 'nuget' | 'npm';
      readonly version: string;
      readonly product: string;
      readonly installKind: InstallKind;
    }[];
  };
};
export function versionLabel(version: ReleaseVersion | undefined) {
  return version?.value ?? 'Version unassigned';
}
export function availabilityLabel(version: ReleaseVersion | undefined) {
  return version?.state === 'published' ? 'Published' : 'Unpublished candidate';
}
export function packageInstallCommand(entry: {
  readonly name: string;
  readonly installKind?: InstallKind;
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
export function createReleaseDocs(data: ReleaseData) {
  const version: ReleaseVersion = {
    state: 'unpublished',
    value: data.currentCandidate.version,
  };
  const catalogRows = data.currentCandidate.packages.map((entry) => ({
    name: entry.identity,
    registry: entry.ecosystem === 'nuget' ? 'NuGet' : 'npm',
    productId: entry.product,
    product: entry.product,
    installKind: entry.installKind,
    version,
  }));
  return {
    catalogRows,
    activeVersionsArePending: true,
    activeVersionForProduct: (product: string) =>
      catalogRows.some((row) => row.productId === product)
        ? version
        : undefined,
  };
}
