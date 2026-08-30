export type ReleaseVersion = {
  readonly state: 'published' | 'unassigned';
  readonly value: string | null;
};

export type ReleaseData = {
  readonly products: readonly {
    readonly id: string;
    readonly stableOwner: string;
    readonly support: 'supported' | 'archived';
    readonly archive?: {
      readonly status: 'archived';
      readonly evidence: {
        readonly repository: string;
        readonly revision: string;
        readonly path: string;
      };
    };
  }[];
  readonly canonicalPackages: readonly {
    readonly identity: string;
    readonly ecosystem: 'nuget' | 'npm';
    readonly product: string;
    readonly state: 'approved' | 'conditional';
    readonly installKind?:
      'nuget-package' | 'dotnet-template' | 'dotnet-tool' | 'npm-package';
  }[];
  readonly currentPackages: readonly {
    readonly identity: string;
    readonly ecosystem: 'nuget' | 'npm';
    readonly product: string;
    readonly stableOwner: string;
    readonly support: 'supported' | 'archived';
    readonly disposition: string;
    readonly target: string | null;
    readonly migration: {
      readonly kind: string;
      readonly target: string | null;
      readonly guidance: string;
    };
  }[];
  readonly compatibilityTrains: readonly {
    readonly id: string;
    readonly lanes: readonly {
      readonly name: string;
      readonly versions: readonly {
        readonly product: string;
        readonly version: ReleaseVersion;
      }[];
    }[];
  }[];
  readonly distributions: readonly {
    readonly product: string;
    readonly identity: string;
    readonly kind: string;
    readonly version: ReleaseVersion;
  }[];
};

export function versionLabel(version: ReleaseVersion | undefined) {
  return version?.state === 'published' ? version.value! : 'Version unassigned';
}

export function availabilityLabel(version: ReleaseVersion | undefined) {
  return version?.state === 'published'
    ? 'Published'
    : 'Pending release — version unassigned';
}

export function packageInstallCommand(entry: {
  readonly name: string;
  readonly installKind?:
    'nuget-package' | 'dotnet-template' | 'dotnet-tool' | 'npm-package';
  readonly version: ReleaseVersion | undefined;
}) {
  if (entry.version?.state !== 'published' || !entry.installKind)
    return undefined;

  switch (entry.installKind) {
    case 'nuget-package':
      return `dotnet add package ${entry.name} --version ${entry.version.value}`;
    case 'dotnet-template':
      return `dotnet new install ${entry.name}::${entry.version.value}`;
    case 'dotnet-tool':
      return `dotnet tool install --global ${entry.name} --version ${entry.version.value}`;
    case 'npm-package':
      return `npm install ${entry.name}@${entry.version.value}`;
  }
}

export function createReleaseDocs(data: ReleaseData) {
  const unassignedVersion: ReleaseVersion = {
    state: 'unassigned',
    value: null,
  };
  const products = new Map(
    data.products.map((product) => [product.id, product]),
  );
  const productName = (id: string) => products.get(id)?.stableOwner ?? id;
  const versionsByLane = new Map(
    data.compatibilityTrains
      .flatMap((train) => train.lanes)
      .map((lane) => [
        lane.name,
        new Map(lane.versions.map((entry) => [entry.product, entry.version])),
      ]),
  );
  const activeLaneForProduct = (product: string) =>
    products.get(product)?.support === 'supported' ? 'current' : undefined;
  const activeVersions = new Map(
    data.products
      .filter((product) => product.support === 'supported')
      .map((product) => {
        const lane = activeLaneForProduct(product.id);
        return [
          product.id,
          (lane ? versionsByLane.get(lane)?.get(product.id) : undefined) ??
            unassignedVersion,
        ];
      }),
  );
  const activeVersionForProduct = (product: string) =>
    activeVersions.get(product);
  const releaseRows = data.compatibilityTrains.flatMap((train) =>
    train.lanes
      .filter((lane) => lane.name === 'current')
      .flatMap((lane) =>
        lane.versions.map((entry) => ({
          productId: entry.product,
          product: productName(entry.product),
          lane: lane.name,
          version: entry.version,
          support: products.get(entry.product)?.support ?? 'supported',
        })),
      ),
  );
  const catalogRows = data.canonicalPackages.map((entry) => ({
    name: entry.identity,
    registry:
      entry.ecosystem === 'nuget' ? ('NuGet' as const) : ('npm' as const),
    productId: entry.product,
    product: productName(entry.product),
    state: entry.state,
    installKind: entry.installKind,
    version: activeVersionForProduct(entry.product),
  }));
  const migrationRows = data.currentPackages
    .filter((entry) => entry.disposition !== 'keep')
    .map((entry) => ({
      source: entry.identity,
      registry: entry.ecosystem === 'nuget' ? 'NuGet' : 'npm',
      productId: entry.product,
      product: productName(entry.product),
      disposition: entry.disposition,
      target: entry.target,
      migrationKind: entry.migration.kind,
      migrationTarget: entry.migration.target,
      guidance: entry.migration.guidance,
    }));
  const compatibilityRows = data.compatibilityTrains.flatMap((train) =>
    train.lanes.flatMap((lane) =>
      lane.versions.map((entry) => ({
        train: train.id,
        lane: lane.name,
        product: productName(entry.product),
        version: entry.version,
      })),
    ),
  );
  const distributionRows = data.distributions.map((distribution) => ({
    productId: distribution.product,
    product: productName(distribution.product),
    identity: distribution.identity,
    kind: distribution.kind,
    version: distribution.version,
  }));
  const activeVersionsArePending = [...activeVersions.values()].every(
    (version) => version.state === 'unassigned',
  );
  const distributionsArePending = distributionRows.every(
    (distribution) => distribution.version.state === 'unassigned',
  );

  return {
    productName,
    activeLaneForProduct,
    activeVersions,
    activeVersionForProduct,
    activeVersionsArePending,
    distributionsArePending,
    releaseSummary: activeVersionsArePending
      ? 'The release authority has not assigned versions to the active compatibility lanes. Pending values remain pending until the authority records a published version.'
      : 'The release authority records the active compatibility-lane versions below. Use each published version exactly as recorded.',
    releaseRows,
    catalogRows,
    migrationRows,
    compatibilityRows,
    distributionRows,
  };
}
